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
    : IGitHubAppScopedWriteCredentialInspector
{
    private const int MaxReplyBytes = 32_768;

    public async Task<GitHubAppWriteScopeObservation> InspectAsync(
        long appId, long installationId, string privateKeyPem,
        string organization, string repository, CancellationToken ct)
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
                ["contents"] = "write"
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
        if (!TryText(node, "token", out var token) ||
            token.Length is < 10 or > 4096 || token.Any(char.IsControl) ||
            !TryText(node, "repository_selection", out var selection) ||
            selection != "selected" ||
            !node.TryGetProperty("permissions", out var permissions) ||
            permissions.ValueKind != JsonValueKind.Object ||
            !permissions.TryGetProperty("contents", out var contents) ||
            contents.ValueKind != JsonValueKind.String ||
            contents.GetString() != "write" ||
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

        // Verify repo identity with the newly issued installation token, not
        // the old read-only PAT. This is still only a GET, not a push check.
        using var metadata = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/" +
            Uri.EscapeDataString(organization) + "/" +
            Uri.EscapeDataString(repository));
        metadata.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        metadata.Headers.UserAgent.ParseAdd("GitHubMigrator/1.0");
        metadata.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            "application/vnd.github+json"));
        metadata.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        using var visible = await client.SendAsync(metadata,
            HttpCompletionOption.ResponseHeadersRead, ct);
        if (visible.StatusCode != HttpStatusCode.OK)
            throw new GitHubAppWriteScopeException("AppScopeRepositoryNotVisible");
        using var reply = await BoundedJsonAsync(visible, ct);
        if (reply is null || reply.RootElement.ValueKind != JsonValueKind.Object ||
            !ValidRepo(reply.RootElement, organization, repository, out var currentId) ||
            currentId != repoId ||
            !reply.RootElement.TryGetProperty("archived", out var archived) ||
            archived.ValueKind != JsonValueKind.False ||
            !reply.RootElement.TryGetProperty("disabled", out var disabled) ||
            disabled.ValueKind != JsonValueKind.False)
            throw new GitHubAppWriteScopeException("AppScopeRepositoryChanged");

        return new(installationId, repoId,
            GitHubAppWriteScopeIntegrity.Issued,
            clock.GetUtcNow(), tokenExpiry.ToUniversalTime());
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
