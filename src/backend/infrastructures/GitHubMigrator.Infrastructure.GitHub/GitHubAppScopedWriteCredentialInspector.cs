using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.GitHub;

// F-043 mint-and-discard: the only outbound non-GET is GitHub's token
// issuance endpoint (POST /app/installations/{id}/access_tokens). No
// repository API write, no Git push, no token returned or persisted.
public sealed class GitHubAppScopedWriteCredentialInspector(
    HttpClient client, TimeProvider clock)
    : IGitHubAppScopedWriteCredentialInspector, IGitHubRemoteAppPreflightReader,
      IGitHubSandboxVerifiedTokenScope, IGitHubRecoveryReadOnlyTokenScope
{
    private const int MaxReplyBytes = 32_768;

    // F-087: read-only recovery mints a SEPARATE token with only
    // contents:read; the legacy F-083 writer token is never reused.
    // A token never escapes the callback or appears in an audit/DTO.
    public async Task<GitHubIndependentRecoveryResult> WithReadOnlyTokenAsync(
        long appId, long installationId, string privateKeyPem,
        string organization, string repository, long repositoryId,
        Func<string, DateTimeOffset, CancellationToken,
            Task<GitHubIndependentRecoveryResult>> action,
        CancellationToken ct)
    {
        if (repositoryId <= 0 || action is null)
            throw new GitHubIndependentRecoveryException(
                "IndependentRecoveryTokenScopeInvalid");
        var minted = await MintVerifiedTokenAsync(
            appId, installationId, privateKeyPem,
            organization, repository, ct, readOnly: true);
        if (minted.Observation.RepositoryId != repositoryId ||
            minted.Observation.InstallationId != installationId ||
            minted.Observation.TokenExpiresAtUtc <=
                clock.GetUtcNow().AddMinutes(2))
            throw new GitHubIndependentRecoveryException(
                "IndependentRecoveryInstallationChanged");
        return await action(minted.Token,
            minted.Observation.TokenExpiresAtUtc, ct);
    }


    // F-083: narrowly scoped, internal-only callback. F-043/F-051 still
    // mint and discard their own tokens without exposing them. The GitHub
    // App token stays in memory inside this sandbox sender callback; neither
    // a public API nor an audit record receives the token or URL.
    public async Task<GitHubSandboxOneShotOutcome> WithVerifiedTokenAsync(
        long appId, long installationId, string privateKeyPem,
        string organization, string repository, long expectedRepositoryId,
        Func<string, CancellationToken, Task<GitHubSandboxOneShotOutcome>> action,
        CancellationToken ct)
    {
        if (expectedRepositoryId <= 0 || action is null)
            throw new GitHubSandboxOneShotException("SandboxSenderTokenScopeInvalid");
        var minted = await MintVerifiedTokenAsync(
            appId, installationId, privateKeyPem,
            organization, repository, ct);
        if (minted.Observation.RepositoryId != expectedRepositoryId ||
            minted.Observation.InstallationId != installationId ||
            minted.Observation.TokenExpiresAtUtc <=
                clock.GetUtcNow().AddMinutes(2))
            throw new GitHubSandboxOneShotException(
                "SandboxSenderInstallationIdentityChanged");
        return await action(minted.Token, ct);
    }

    public async Task<GitHubAppWriteScopeObservation> InspectAsync(
        long appId, long installationId, string privateKeyPem,
        string organization, string repository, CancellationToken ct) =>
        (await MintVerifiedTokenAsync(appId, installationId, privateKeyPem,
            organization, repository, ct)).Observation;

    // F-051 consumes a newly minted one-repository GitHub App token only
    // INSIDE this infrastructure adapter, making strictly GET-only calls.
    // No access token can escape via a domain DTO or persistence method.
    public async Task<GitHubRemoteAppPreflightObservation> InspectAsync(
        long appId, long installationId, string privateKeyPem,
        GitHubCreateOnlyCandidatePacket packet, CancellationToken ct)
    {
        if (packet is null || packet.Packet is null ||
            packet.Candidates is null ||
            packet.Candidates.Count is < 1 or >
                GitHubCandidatePolicyIntegrity.MaxOnlineCandidates ||
            packet.Packet.State != GitHubCreateOnlyPacketIntegrity.State ||
            packet.Packet.ExecutionAuthorized || packet.Packet.RemoteWritesPerformed ||
            packet.Packet.WriteCapabilityVerified || packet.Packet.ReadyForExecution ||
            packet.Candidates.Count != packet.Packet.CreateCandidates ||
            packet.Packet.CandidateSetSha256 !=
                GitImportRehearsalIntegrity.SealRefs(
                    "f044-create-only-candidate-refs-v1", packet.Candidates))
            throw new GitHubRemoteAppPreflightException(
                "RemotePreflightPacketInvalid");

        var target = packet.Packet;
        var minted = await MintVerifiedTokenAsync(
            appId, installationId, privateKeyPem,
            target.Organization, target.Repository, ct);
        if (minted.Observation.RepositoryId != target.RepositoryId ||
            minted.Observation.InstallationId != installationId)
            throw new GitHubRemoteAppPreflightException(
                "RemotePreflightRepositoryIdentityChanged");

        // Each reader has only fixed api.github.com GET paths. Even the
        // GitHub App's contents:write token cannot enter a mutation path.
        var refs = await new GitHubRefObservationReader(client, clock).ReadAsync(
            target.Organization, target.Repository, minted.Token, ct);
        var policies = await new GitHubCandidatePolicyReader(client, clock)
            .ReadAsync(target.Organization, target.Repository,
                target.RepositoryId, minted.Token, packet.Candidates, ct);

        // F-073: policy inspection is NOT an atomic GitHub snapshot.
        // Re-read the entire typed head/tag ref set after the policy GETs.
        // A branch, annotated tag or baseline ref changed mid-review
        // must never yield a sealed F-051 preflight receipt.
        var finalRefs = await new GitHubRefObservationReader(client, clock)
            .ReadAsync(target.Organization, target.Repository,
                minted.Token, ct);
        if (!refs.Refs.SequenceEqual(finalRefs.Refs))
            throw new GitHubRemoteAppPreflightException(
                "RemotePreflightRefsChangedDuringReview");

        // Verify the SAME numeric GitHub repository and owner again last.
        // An organization rename, archive, disable or repository swap
        // between the first metadata GET and the final ref GET fails closed.
        try
        {
            await VerifyRepositoryWithTokenAsync(minted.Token,
                target.Organization, target.Repository,
                target.RepositoryId, ct);
        }
        catch (GitHubAppWriteScopeException)
        {
            throw new GitHubRemoteAppPreflightException(
                "RemotePreflightRepositoryChangedDuringReview");
        }
        return new(installationId, target.RepositoryId, finalRefs,
            policies, minted.Observation.TokenExpiresAtUtc);
    }

    private async Task<(GitHubAppWriteScopeObservation Observation,
        string Token)> MintVerifiedTokenAsync(
        long appId, long installationId, string privateKeyPem,
        string organization, string repository, CancellationToken ct,
        bool readOnly = false)
    {
        if (appId <= 0 || installationId <= 0 ||
            !GitHubRefObservationIntegrity.ValidOwner(organization) ||
            !GitHubRefObservationIntegrity.ValidRepository(repository) ||
            string.IsNullOrWhiteSpace(privateKeyPem) ||
            privateKeyPem.Length > 32_768)
            throw new GitHubAppWriteScopeException("AppScopeInspectionInvalid");

        var started = clock.GetUtcNow();
        string appJwt;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(privateKeyPem);
            var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(
                new { alg = "RS256", typ = "JWT" }));
            var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new {
                iat = started.AddSeconds(-60).ToUnixTimeSeconds(),
                exp = started.AddMinutes(8).ToUnixTimeSeconds(),
                iss = appId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }));
            var signingInput = header + "." + payload;
            var sig = rsa.SignData(Encoding.ASCII.GetBytes(signingInput),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            appJwt = signingInput + "." + Base64Url(sig);
        }
        catch (CryptographicException)
        { throw new GitHubAppWriteScopeException("AppScopeSigningKeyInvalid"); }
        catch (ArgumentException)
        { throw new GitHubAppWriteScopeException("AppScopeSigningKeyInvalid"); }

        // F-072: verify the App's actual installation account using the
        // already signed App JWT BEFORE minting even a restricted token.
        // A configured numeric installation ID alone cannot establish
        // ownership by the intended organization.
        await VerifyInstallationAsync(appId, installationId,
            organization, appJwt, ct);

        using var mint = new HttpRequestMessage(HttpMethod.Post,
            "https://api.github.com/app/installations/" +
            installationId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            "/access_tokens");
        mint.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appJwt);
        mint.Headers.UserAgent.ParseAdd("GitHubMigrator/1.0");
        mint.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            "application/vnd.github+json"));
        mint.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        // Single named repository, exactly Contents:write, with GitHub's
        // implicit Metadata:read. No workflow/admin/organization permissions.
        mint.Content = new StringContent(JsonSerializer.Serialize(new {
            repositories = new[] { repository },
            permissions = new Dictionary<string, string> {
                ["contents"] = readOnly ? "read" : "write"
            }
        }), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(mint,
            HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.Created)
            throw new GitHubAppWriteScopeException("AppScopeMintRejected");
        using var minted = await BoundedJsonAsync(response, ct);
        if (minted is null || minted.RootElement.ValueKind != JsonValueKind.Object)
            throw new GitHubAppWriteScopeException("AppScopeMintMalformed");

        var node = minted.RootElement;
        // System.Text.Json accepts duplicate object members and TryGetProperty
        // resolves the last occurrence. A contradictory GitHub response must
        // never become a positive permission attestation.
        if (!HasUniqueProperties(node) ||
            !TryText(node, "token", out var token) ||
            token.Length is < 10 or > 4096 || token.Any(char.IsControl) ||
            !TryText(node, "repository_selection", out var selection) ||
            selection != "selected" ||
            !node.TryGetProperty("permissions", out var permissions) ||
            permissions.ValueKind != JsonValueKind.Object ||
            !permissions.TryGetProperty("contents", out var contents) ||
            contents.ValueKind != JsonValueKind.String ||
            contents.GetString() != (readOnly ? "read" : "write") ||
            permissions.EnumerateObject().Any(p =>
                p.Name is not ("contents" or "metadata") ||
                (p.Name == "metadata" &&
                    (p.Value.ValueKind != JsonValueKind.String ||
                     p.Value.GetString() != "read"))) ||
            !node.TryGetProperty("repositories", out var granted) ||
            granted.ValueKind != JsonValueKind.Array ||
            granted.GetArrayLength() != 1 ||
            !ValidRepo(granted[0], organization, repository, out var repoId) ||
            !TryText(node, "expires_at", out var expires) ||
            !DateTimeOffset.TryParse(expires,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var tokenExpiry))
            throw new GitHubAppWriteScopeException("AppScopeMintNotRestricted");

        var now = clock.GetUtcNow();
        if (tokenExpiry <= now.AddMinutes(3) ||
            tokenExpiry > now.AddMinutes(65))
            throw new GitHubAppWriteScopeException("AppScopeExpiryInvalid");

        // The initial token-bound repository GET is also reused by
        // F-073 at the END of the F-051 refs + policy inspection.
        await VerifyRepositoryWithTokenAsync(
            token, organization, repository, repoId, ct);

        return (new GitHubAppWriteScopeObservation(
            installationId, repoId, GitHubAppWriteScopeIntegrity.Issued,
            clock.GetUtcNow(), tokenExpiry.ToUniversalTime()), token);
    }

    // F-073: return no metadata, token or path to domain callers.
    // A late check must use the SAME restricted token, not a PAT.
    private async Task VerifyRepositoryWithTokenAsync(
        string token, string organization, string repository,
        long expectedRepositoryId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/" +
            Uri.EscapeDataString(organization) + "/" +
            Uri.EscapeDataString(repository));
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("GitHubMigrator/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            "application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation(
            "X-GitHub-Api-Version", "2022-11-28");
        using var reply = await client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, ct);
        if (reply.StatusCode != HttpStatusCode.OK)
            throw new GitHubAppWriteScopeException(
                "AppScopeRepositoryNotVisible");
        using var json = await BoundedJsonAsync(reply, ct);
        if (json is null ||
            json.RootElement.ValueKind != JsonValueKind.Object ||
            !HasUniqueProperties(json.RootElement) ||
            !ValidRepo(json.RootElement, organization, repository,
                out var currentId) ||
            currentId != expectedRepositoryId ||
            !json.RootElement.TryGetProperty("archived", out var archived) ||
            archived.ValueKind != JsonValueKind.False ||
            !json.RootElement.TryGetProperty("disabled", out var disabled) ||
            disabled.ValueKind != JsonValueKind.False)
            throw new GitHubAppWriteScopeException(
                "AppScopeRepositoryChanged");
    }

    // F-072: App JWT-only GET. This response is never stored, logged or
    // returned; the sole subsequent POST is still one-repository token
    // issuance with exact Contents:write and implicit Metadata:read.
    private async Task VerifyInstallationAsync(long appId,
        long installationId, string organization, string appJwt,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/app/installations/" +
            installationId.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", appJwt);
        request.Headers.UserAgent.ParseAdd("GitHubMigrator/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            "application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation(
            "X-GitHub-Api-Version", "2022-11-28");

        using var response = await client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new GitHubAppWriteScopeException(
                "AppScopeInstallationNotVerified");
        using var document = await BoundedJsonAsync(response, ct);
        if (document is null ||
            document.RootElement.ValueKind != JsonValueKind.Object)
            throw new GitHubAppWriteScopeException(
                "AppScopeInstallationMismatch");
        var node = document.RootElement;
        if (!HasUniqueProperties(node) ||
            !node.TryGetProperty("id", out var identity) ||
            identity.ValueKind != JsonValueKind.Number ||
            !identity.TryGetInt64(out var actualInstallationId) ||
            actualInstallationId != installationId ||
            !node.TryGetProperty("app_id", out var app) ||
            app.ValueKind != JsonValueKind.Number ||
            !app.TryGetInt64(out var actualAppId) ||
            actualAppId != appId ||
            !node.TryGetProperty("target_id", out var targetId) ||
            targetId.ValueKind != JsonValueKind.Number ||
            !targetId.TryGetInt64(out var targetAccountId) ||
            targetAccountId <= 0 ||
            !TryText(node, "target_type", out var targetType) ||
            targetType != "Organization" ||
            !TryText(node, "repository_selection", out var selection) ||
            selection is not ("selected" or "all") ||
            !node.TryGetProperty("suspended_at", out var suspended) ||
            suspended.ValueKind != JsonValueKind.Null ||
            !node.TryGetProperty("account", out var account) ||
            account.ValueKind != JsonValueKind.Object ||
            !account.TryGetProperty("id", out var accountId) ||
            accountId.ValueKind != JsonValueKind.Number ||
            !accountId.TryGetInt64(out var accountNumericId) ||
            accountNumericId != targetAccountId ||
            !TryText(account, "login", out var login) ||
            !string.Equals(login, organization,
                StringComparison.OrdinalIgnoreCase) ||
            !TryText(account, "type", out var accountType) ||
            accountType != "Organization" ||
            !node.TryGetProperty("permissions", out var permissions) ||
            permissions.ValueKind != JsonValueKind.Object ||
            !TryText(permissions, "contents", out var grantedContents) ||
            grantedContents != "write")
            throw new GitHubAppWriteScopeException(
                "AppScopeInstallationMismatch");
    }

    // Validate recursively: even duplicate nested owner/permission fields are
    // ambiguous. Both JSON responses are bounded to 32 KiB before parsing.
    private static bool HasUniqueProperties(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
                if (!names.Add(property.Name) || !HasUniqueProperties(property.Value))
                    return false;
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in node.EnumerateArray())
                if (!HasUniqueProperties(child)) return false;
        }
        return true;
    }

    private static bool ValidRepo(JsonElement node, string org, string repo,
        out long id)
    {
        id = 0;
        return node.ValueKind == JsonValueKind.Object &&
            node.TryGetProperty("id", out var numeric) &&
            numeric.ValueKind == JsonValueKind.Number &&
            numeric.TryGetInt64(out id) && id > 0 &&
            TryText(node, "full_name", out var full) &&
            string.Equals(full, org + "/" + repo, StringComparison.OrdinalIgnoreCase) &&
            node.TryGetProperty("owner", out var owner) &&
            owner.ValueKind == JsonValueKind.Object &&
            TryText(owner, "login", out var login) &&
            string.Equals(login, org, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryText(JsonElement node, string name, out string value)
    {
        value = "";
        if (!node.TryGetProperty(name, out var field) ||
            field.ValueKind != JsonValueKind.String) return false;
        value = field.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(value);
    }

    private static async Task<JsonDocument?> BoundedJsonAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > MaxReplyBytes)
            return null;
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
        // JsonDocument.Parse(ReadOnlyMemory<byte>) may borrow the input
        // buffer. Copy only the bounded payload into document-owned storage
        // BEFORE zeroing the temporary read buffer.
        try { return JsonDocument.Parse(bytes.AsMemory(0, length).ToArray()); }
        catch (JsonException) { return null; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
