using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GitHubMigrator.Business;
using GitHubMigrator.Business.WorkflowGates;

namespace GitHubMigrator.Infrastructure.GitHub;

// The only code in the product that writes a workflow file or opens a pull request. It writes ONE file, under .github/workflows/, on a NEW
// branch whose name it checks, with a token it mints for ONE repository with exactly contents, pull_requests and workflows write and discards
// when it returns. The file is either new (it must not exist) or the replacement of the one text the plan names (it must exist, with that blob id and
// that text, at the planned base commit): the base branch itself is never written. It never merges, never force-pushes, never deletes, and never
// repeats a write: a write whose answer was lost is reported as such (WritesPossible) and left for a person. Everything it does is read back before it
// says it succeeded.
public sealed partial class GitHubPublicationWriter(HttpClient client, TimeProvider clock, GitHubEndpoints? endpoints = null) : IGitHubPublicationWriter
{
    private const int MaxReplyBytes = 262_144;
    private static readonly string[] ExpectedPermissions = ["contents:write", "metadata:read", "pull_requests:write", "workflows:write"];
    private GitHubEndpoints Endpoints => endpoints ?? GitHubEndpoints.Cloud;

    [GeneratedRegex(@"^\.github/workflows/azdo-[0-9]{1,9}-[a-z0-9-]{1,60}\.yml$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex WorkflowPath();
    [GeneratedRegex(@"^migrate/azdo-[0-9]{1,9}-[0-9a-f]{8}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex HeadBranch();
    [GeneratedRegex(@"^[0-9a-f]{40}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex GitSha();
    [GeneratedRegex(@"^[0-9a-f]{64}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex Sha256Hex();

    public async Task<PublicationCredentialObservation> InspectAsync(long appId, long installationId, string privateKeyPem, string organization, string repository, CancellationToken ct) =>
        (await MintAsync(appId, installationId, privateKeyPem, organization, repository, ct)).Observation;

    public async Task<PublicationOutcome> PublishAsync(long appId, long installationId, string privateKeyPem, PublicationExecution plan, IPublicationProgress progress, CancellationToken ct)
    {
        Validate(plan);
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(TimeSpan.FromMinutes(2));
        ct = overall.Token;

        var minted = await MintAsync(appId, installationId, privateKeyPem, plan.Organization, plan.Repository, ct);
        var token = minted.Token;
        var root = $"repos/{Uri.EscapeDataString(plan.Organization)}/{Uri.EscapeDataString(plan.Repository)}";
        var bytes = new UTF8Encoding(false).GetBytes(plan.WorkflowYaml);

        // ---- reads with the writing credential: nothing has been written yet, so every refusal here leaves nothing behind
        using (var tip = await ReadAsync($"{root}/branches/{PathEscape(plan.BaseBranch)}", token, ct))
            if (tip is null || Text(Property(tip.RootElement, "commit"), "sha") != plan.BaseSha) throw new PublicationWriteException("PublicationBaseMoved", false);
        var update = plan.Mode == PublicationModes.Update;
        using (var file = await ReadAsync($"{root}/contents/{PathEscape(plan.WorkflowPath)}?ref={plan.BaseSha}", token, ct))
        {
            // a new file must not exist; a replacement must find exactly the blob and the text the plan was made against, or somebody changed it since
            if (!update && file is not null) throw new PublicationWriteException("PublicationWorkflowPathOccupied", false);
            if (update && (file is null || !IsReplaceable(file.RootElement, plan))) throw new PublicationWriteException("PublicationWorkflowPathChanged", false);
        }
        using (var head = await ReadAsync($"{root}/git/ref/heads/{PathEscape(plan.HeadBranch)}", token, ct))
            if (head is not null) throw new PublicationWriteException("PublicationHeadBranchExists", false);

        // ---- 1. the branch, at the commit the plan was made against
        await progress.RecordAsync(PublicationStates.BranchCreating, null, null, null, ct);
        using (var branch = await WriteAsync($"{root}/git/refs", token, new { @ref = "refs/heads/" + plan.HeadBranch, sha = plan.BaseSha }, "PublicationBranch", writesBefore: false, ct))
            if (Text(Property(branch.RootElement, "object"), "sha") != plan.BaseSha) throw new PublicationWriteException("PublicationBranchUnexpected", true);
        await progress.RecordAsync(PublicationStates.BranchCreated, null, null, null, ct);

        // ---- 2. the one file, on that branch only
        await progress.RecordAsync(PublicationStates.FileCommitting, null, null, null, ct);
        string commitSha;
        // GitHub answers 201 for a created file and 200 for a replaced one, and a replacement must name the blob it replaces
        object fileBody = update
            ? new { message = plan.CommitMessage, content = Convert.ToBase64String(bytes), branch = plan.HeadBranch, sha = plan.ReplacesBlobSha }
            : new { message = plan.CommitMessage, content = Convert.ToBase64String(bytes), branch = plan.HeadBranch };
        using (var committed = await WriteAsync($"{root}/contents/{PathEscape(plan.WorkflowPath)}", token, fileBody, "PublicationFile", writesBefore: true, ct, HttpMethod.Put,
            update ? HttpStatusCode.OK : HttpStatusCode.Created))
        {
            commitSha = Text(Property(committed.RootElement, "commit"), "sha") ?? "";
            var blob = Text(Property(committed.RootElement, "content"), "sha");
            var parents = Property(Property(committed.RootElement, "commit"), "parents");
            if (!GitSha().IsMatch(commitSha) || blob != BlobSha(bytes) ||
                parents.ValueKind != JsonValueKind.Array || parents.GetArrayLength() != 1 || Text(parents[0], "sha") != plan.BaseSha)
                throw new PublicationWriteException("PublicationFileUnexpected", true);
        }
        await progress.RecordAsync(PublicationStates.FileCommitted, commitSha, null, null, ct);

        // ---- 3. the pull request, from that branch into the base it was planned against
        await progress.RecordAsync(PublicationStates.PullRequestOpening, commitSha, null, null, ct);
        int number; string? url;
        using (var opened = await WriteAsync($"{root}/pulls", token, new { title = plan.PullRequestTitle, body = plan.PullRequestBody, head = plan.HeadBranch, @base = plan.BaseBranch }, "PublicationPullRequest", writesBefore: true, ct))
        {
            if (!opened.RootElement.TryGetProperty("number", out var n) || !n.TryGetInt32(out number) || number <= 0) throw new PublicationWriteException("PublicationPullRequestUnexpected", true);
            url = Text(opened.RootElement, "html_url");
            // a link to anywhere but the configured GitHub is not stored
            if (url is not null && !url.StartsWith(Endpoints.WebRoot + "/", StringComparison.Ordinal)) url = null;
        }
        await progress.RecordAsync(PublicationStates.PullRequestOpened, commitSha, number, url, ct);

        // ---- 4. read it all back: the pull request is open from our branch at our commit, and the file at that commit is the approved text
        try
        {
            using var pull = await ReadAsync($"{root}/pulls/{number}", token, ct);
            if (pull is null || Text(pull.RootElement, "state") != "open" || Text(Property(pull.RootElement, "head"), "ref") != plan.HeadBranch ||
                Text(Property(pull.RootElement, "head"), "sha") != commitSha || Text(Property(pull.RootElement, "base"), "ref") != plan.BaseBranch)
                throw new PublicationWriteException("PublicationVerificationFailed", true);
            using var file = await ReadAsync($"{root}/contents/{PathEscape(plan.WorkflowPath)}?ref={commitSha}", token, ct);
            if (file is null || DecodedHash(file.RootElement) != plan.WorkflowSha256) throw new PublicationWriteException("PublicationVerificationFailed", true);
        }
        // the pull request exists by now: whatever stops the read-back (a refusal, a lost connection, an unreadable answer) is "written, not verified", never "nothing was written"
        catch (Exception ex) when (ex is PublicationWriteException or HttpRequestException or JsonException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        { throw new PublicationWriteException("PublicationVerificationFailed", true); }
        return new(commitSha, number, url);
    }

    // The writer writes only what the executor decided, and refuses anything shaped differently: one file of a fixed name pattern, a fixed branch pattern.
    private static void Validate(PublicationExecution plan)
    {
        if (!GitHubRefObservationIntegrity.ValidOwner(plan.Organization) || !GitHubRefObservationIntegrity.ValidRepository(plan.Repository) ||
            !WorkflowPath().IsMatch(plan.WorkflowPath) || !HeadBranch().IsMatch(plan.HeadBranch) || !GitSha().IsMatch(plan.BaseSha) ||
            string.IsNullOrWhiteSpace(plan.BaseBranch) || plan.BaseBranch.Length > 200 || plan.BaseBranch.StartsWith('/') || plan.BaseBranch.Contains("..") ||
            string.IsNullOrWhiteSpace(plan.WorkflowYaml) || plan.WorkflowYaml.Length > 262_144 || WorkflowHash.Sha256(plan.WorkflowYaml) != plan.WorkflowSha256 ||
            string.IsNullOrWhiteSpace(plan.PullRequestTitle) || plan.PullRequestTitle.Length > 256 || plan.PullRequestBody.Length > 60_000 || string.IsNullOrWhiteSpace(plan.CommitMessage))
            throw new PublicationWriteException("PublicationRequestInvalid", false);
        // a mode is one of two, and a replacement names both the blob and the text it replaces (a new file names neither)
        var update = plan.Mode == PublicationModes.Update;
        if (plan.Mode is not (PublicationModes.Create or PublicationModes.Update) || update != (plan.ReplacesBlobSha is not null) || update != (plan.ReplacesWorkflowSha256 is not null) ||
            (update && (!GitSha().IsMatch(plan.ReplacesBlobSha!) || !Sha256Hex().IsMatch(plan.ReplacesWorkflowSha256!) || plan.ReplacesWorkflowSha256 == plan.WorkflowSha256)))
            throw new PublicationWriteException("PublicationRequestInvalid", false);
    }

    // The file at the base commit is the one the plan replaces: a regular file whose blob id is the planned one, whose bytes have that id, and whose text is the text named.
    private static bool IsReplaceable(JsonElement file, PublicationExecution plan)
    {
        if (Text(file, "type") != "file" || Text(file, "encoding") != "base64" || Text(file, "content") is not { } encoded || Text(file, "sha") != plan.ReplacesBlobSha) return false;
        try
        {
            var bytes = Convert.FromBase64String(encoded.Replace("\n", "").Replace("\r", ""));
            return GitBlob.Sha(bytes) == plan.ReplacesBlobSha && WorkflowHash.Sha256(new UTF8Encoding(false, true).GetString(bytes)) == plan.ReplacesWorkflowSha256;
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException) { return false; }
    }

    // ---- the credential --------------------------------------------------------------------------------------------------------------------

    private sealed record Minted(string Token, PublicationCredentialObservation Observation);

    private async Task<Minted> MintAsync(long appId, long installationId, string pem, string organization, string repository, CancellationToken ct)
    {
        if (appId <= 0 || installationId <= 0 || !GitHubRefObservationIntegrity.ValidOwner(organization) || !GitHubRefObservationIntegrity.ValidRepository(repository) ||
            string.IsNullOrWhiteSpace(pem) || pem.Length > 32_768)
            throw new PublicationWriteException("PublicationCredentialInvalid", false);

        var started = clock.GetUtcNow();
        string jwt;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
            var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
            {
                iat = started.AddSeconds(-60).ToUnixTimeSeconds(), exp = started.AddMinutes(8).ToUnixTimeSeconds(),
                iss = appId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }));
            jwt = header + "." + payload + "." + Base64Url(rsa.SignData(Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException) { throw new PublicationWriteException("PublicationSigningKeyInvalid", false); }

        await VerifyInstallationAsync(appId, installationId, organization, jwt, ct);

        using var mint = Request(HttpMethod.Post, $"app/installations/{installationId.ToString(System.Globalization.CultureInfo.InvariantCulture)}/access_tokens", jwt);
        mint.Content = new StringContent(JsonSerializer.Serialize(new
        {
            repositories = new[] { repository },
            permissions = new Dictionary<string, string> { ["contents"] = "write", ["pull_requests"] = "write", ["workflows"] = "write" }
        }), Encoding.UTF8, "application/json");
        using var response = await Send(mint, "PublicationMintFailed", ct);
        if (response.StatusCode != HttpStatusCode.Created) throw new PublicationWriteException("PublicationMintRejected", false);
        using var json = await BoundedAsync(response, ct) ?? throw new PublicationWriteException("PublicationMintMalformed", false);
        var node = json.RootElement;
        var granted = node.ValueKind == JsonValueKind.Object && node.TryGetProperty("permissions", out var p) && p.ValueKind == JsonValueKind.Object
            ? p.EnumerateObject().Where(x => x.Value.ValueKind == JsonValueKind.String).Select(x => $"{x.Name}:{x.Value.GetString()}").Order(StringComparer.Ordinal).ToArray() : [];
        // exactly what was asked for and nothing else, one repository, selected: a broader answer is not a token this product uses
        if (!HasUniqueProperties(node) || !TryText(node, "token", out var token) || token.Length is < 10 or > 4096 || token.Any(char.IsControl) ||
            !TryText(node, "repository_selection", out var selection) || selection != "selected" ||
            !granted.SequenceEqual(ExpectedPermissions) ||
            !node.TryGetProperty("repositories", out var repos) || repos.ValueKind != JsonValueKind.Array || repos.GetArrayLength() != 1 ||
            !ValidRepo(repos[0], organization, repository, out var repositoryId) ||
            !TryText(node, "expires_at", out var expiresText) ||
            !DateTimeOffset.TryParse(expiresText, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var expires))
            throw new PublicationWriteException("PublicationMintNotRestricted", false);
        var now = clock.GetUtcNow();
        if (expires <= now.AddMinutes(3) || expires > now.AddMinutes(65)) throw new PublicationWriteException("PublicationExpiryInvalid", false);

        // the token sees the same, live, writable repository
        using var request = Request(HttpMethod.Get, $"repos/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(repository)}", token);
        using var reply = await Send(request, "PublicationRepositoryNotVisible", ct);
        if (reply.StatusCode != HttpStatusCode.OK) throw new PublicationWriteException("PublicationRepositoryNotVisible", false);
        using var repoJson = await BoundedAsync(reply, ct);
        if (repoJson is null || !HasUniqueProperties(repoJson.RootElement) || !ValidRepo(repoJson.RootElement, organization, repository, out var currentId) || currentId != repositoryId ||
            !repoJson.RootElement.TryGetProperty("archived", out var archived) || archived.ValueKind != JsonValueKind.False ||
            !repoJson.RootElement.TryGetProperty("disabled", out var disabled) || disabled.ValueKind != JsonValueKind.False)
            throw new PublicationWriteException("PublicationRepositoryChanged", false);

        return new(token, new(installationId, repositoryId, ExpectedPermissions, expires.ToUniversalTime()));
    }

    private async Task VerifyInstallationAsync(long appId, long installationId, string organization, string jwt, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, "app/installations/" + installationId.ToString(System.Globalization.CultureInfo.InvariantCulture), jwt);
        using var response = await Send(request, "PublicationInstallationNotVerified", ct);
        if (response.StatusCode != HttpStatusCode.OK) throw new PublicationWriteException("PublicationInstallationNotVerified", false);
        using var document = await BoundedAsync(response, ct);
        var n = document?.RootElement ?? default;
        if (document is null || n.ValueKind != JsonValueKind.Object || !HasUniqueProperties(n) ||
            !n.TryGetProperty("id", out var id) || !id.TryGetInt64(out var actualId) || actualId != installationId ||
            !n.TryGetProperty("app_id", out var app) || !app.TryGetInt64(out var actualApp) || actualApp != appId ||
            !TryText(n, "target_type", out var targetType) || targetType != "Organization" ||
            !n.TryGetProperty("suspended_at", out var suspended) || suspended.ValueKind != JsonValueKind.Null ||
            !n.TryGetProperty("account", out var account) || account.ValueKind != JsonValueKind.Object ||
            !TryText(account, "login", out var login) || !string.Equals(login, organization, StringComparison.OrdinalIgnoreCase) ||
            !n.TryGetProperty("permissions", out var permissions) || permissions.ValueKind != JsonValueKind.Object ||
            Text(permissions, "contents") != "write" || Text(permissions, "pull_requests") != "write" || Text(permissions, "workflows") != "write")
            throw new PublicationWriteException("PublicationInstallationMismatch", false);
    }

    // ---- HTTP ----------------------------------------------------------------------------------------------------------------------------

    private HttpRequestMessage Request(HttpMethod method, string relative, string bearer)
    {
        var request = new HttpRequestMessage(method, Endpoints.Api(relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.UserAgent.ParseAdd("GitHubMigrator/0.4");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    // Before anything was written, a transport failure is simply a refusal.
    private async Task<HttpResponseMessage> Send(HttpRequestMessage request, string code, CancellationToken ct)
    {
        try { return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { throw new PublicationWriteException(code, false); }
    }

    // null is a 404; anything else that is not a success is a refusal that left nothing behind (reads only).
    private async Task<JsonDocument?> ReadAsync(string relative, string token, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, relative, token);
        using var response = await Send(request, "PublicationReadFailed", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode != HttpStatusCode.OK) throw new PublicationWriteException("PublicationReadFailed", false);
        return await BoundedAsync(response, ct) ?? throw new PublicationWriteException("PublicationReadFailed", false);
    }

    // A write. A definite 4xx means it did not happen; a transport failure, a timeout, a 5xx or an unreadable answer means it may have, and is
    // reported that way. It is never repeated.
    private async Task<JsonDocument> WriteAsync(string relative, string token, object body, string code, bool writesBefore, CancellationToken ct, HttpMethod? method = null,
        HttpStatusCode? success = null)
    {
        using var request = Request(method ?? HttpMethod.Post, relative, token);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { throw new PublicationWriteException(code + "AnswerLost", true); }
        using (response)
        {
            // only the one success the step expects: any other 2xx means the write happened in a way this writer did not plan for
            if (response.StatusCode != (success ?? HttpStatusCode.Created))
                throw new PublicationWriteException(code + (response.StatusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError ? "Rejected" : "Failed"),
                    writesBefore || (int)response.StatusCode >= 500 || response.IsSuccessStatusCode);
            try { return await BoundedAsync(response, ct) ?? throw new PublicationWriteException(code + "AnswerUnreadable", true); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { throw new PublicationWriteException(code + "AnswerLost", true); }
        }
    }

    private static async Task<JsonDocument?> BoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > MaxReplyBytes) return null;
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var bytes = new byte[MaxReplyBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var n = await body.ReadAsync(bytes.AsMemory(length), ct);
            if (n == 0) break;
            length += n;
        }
        if (length > MaxReplyBytes) return null;
        try { return JsonDocument.Parse(bytes.AsMemory(0, length).ToArray()); }
        catch (JsonException) { return null; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    // ---- small helpers -------------------------------------------------------------------------------------------------------------------

    private static string BlobSha(byte[] bytes) => GitBlob.Sha(bytes);

    private static string? DecodedHash(JsonElement file)
    {
        if (Text(file, "type") != "file" || Text(file, "encoding") != "base64" || Text(file, "content") is not { } encoded) return null;
        try { return WorkflowHash.Sha256(new UTF8Encoding(false, true).GetString(Convert.FromBase64String(encoded.Replace("\n", "").Replace("\r", "")))); }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException) { return null; }
    }

    private static string PathEscape(string value) => string.Join('/', value.Split('/').Select(Uri.EscapeDataString));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static JsonElement Property(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;
    private static string? Text(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool TryText(JsonElement node, string name, out string value)
    {
        value = Text(node, name) ?? "";
        return !string.IsNullOrWhiteSpace(value);
    }

    // Duplicate members are ambiguous (the parser keeps the last): a contradictory answer is never a positive one.
    private static bool HasUniqueProperties(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
                if (!names.Add(property.Name) || !HasUniqueProperties(property.Value)) return false;
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var child in node.EnumerateArray())
                if (!HasUniqueProperties(child)) return false;
        return true;
    }

    private static bool ValidRepo(JsonElement node, string organization, string repository, out long id)
    {
        id = 0;
        return node.ValueKind == JsonValueKind.Object && node.TryGetProperty("id", out var numeric) && numeric.ValueKind == JsonValueKind.Number && numeric.TryGetInt64(out id) && id > 0 &&
            TryText(node, "full_name", out var full) && string.Equals(full, organization + "/" + repository, StringComparison.OrdinalIgnoreCase) &&
            node.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.Object && TryText(owner, "login", out var login) &&
            string.Equals(login, organization, StringComparison.OrdinalIgnoreCase);
    }
}
