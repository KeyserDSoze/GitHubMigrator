using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.GitHub;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubAppWriteCredentialInspectorTests
{
    private const string RepoName = "sandbox";
    private const string Owner = "gh-org";
    private const string AccessToken = "ghs_INTERNAL_TEST_TOKEN_NOT_FOR_EXPORT";

    [Fact]
    public async Task App_jwt_signs_correctly_and_mints_exact_one_repo_content_permission()
    {
        using var rsa = RSA.Create(2048);
        var privateKey = rsa.ExportPkcs8PrivateKeyPem();
        var calls = 0;
        using var client = new HttpClient(new Handler(req =>
        {
            calls++;
            Assert.Equal("api.github.com", req.RequestUri!.Host);
            if (calls == 1)
            {
                Assert.Equal(HttpMethod.Get, req.Method);
                Assert.Equal("/app/installations/77", req.RequestUri!.AbsolutePath);
                Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
                return Json(200, InstallationReply());
            }
            if (calls == 2)
            {
                Assert.Equal(HttpMethod.Post, req.Method);
                Assert.Equal("/app/installations/77/access_tokens",
                    req.RequestUri.AbsolutePath);
                Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
                var jwt = req.Headers.Authorization!.Parameter!;
                Assert.Equal(3, jwt.Split('.').Length);
                using var header = JsonDocument.Parse(DecodePart(jwt.Split('.')[0]));
                using var payload = JsonDocument.Parse(DecodePart(jwt.Split('.')[1]));
                Assert.Equal("RS256",
                    header.RootElement.GetProperty("alg").GetString());
                Assert.Equal("1234", payload.RootElement.GetProperty("iss").GetString());
                Assert.True(rsa.VerifyData(
                    Encoding.ASCII.GetBytes(jwt[..jwt.LastIndexOf('.')]),
                    DecodePart(jwt.Split('.')[2]),
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
                using var json = JsonDocument.Parse(req.Content!.ReadAsStringAsync()
                    .GetAwaiter().GetResult());
                Assert.Equal(RepoName,
                    json.RootElement.GetProperty("repositories")[0].GetString());
                Assert.Equal("write", json.RootElement
                    .GetProperty("permissions").GetProperty("contents").GetString());
                Assert.Single(json.RootElement.GetProperty("permissions").EnumerateObject());
                return Json(201, MintReply());
            }
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Equal("/repos/gh-org/sandbox", req.RequestUri.AbsolutePath);
            Assert.Equal(AccessToken, req.Headers.Authorization?.Parameter);
            return Json(200, RepoReply());
        }));
        var observed = await new GitHubAppScopedWriteCredentialInspector(
            client, TimeProvider.System)
            .InspectAsync(1234, 77, privateKey, Owner, RepoName, default);
        Assert.Equal(GitHubAppWriteScopeIntegrity.Issued, observed.Status);
        Assert.Equal(77, observed.InstallationId);
        Assert.Equal(2468, observed.RepositoryId);
        Assert.Equal(3, calls);
        var metadata = new GitHubAppWriteScopeInput(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Owner, RepoName, Sha, Sha, Sha, Sha, Sha,
            Sha, observed, Guid.NewGuid());
        var digest = GitHubAppWriteScopeIntegrity.Seal(metadata);
        Assert.Equal(64, digest.Length);
        Assert.NotEqual(digest, GitHubAppWriteScopeIntegrity.Seal(
            metadata with { FenceEvidenceSha256 = new string('d',64) }));
        Assert.DoesNotContain(AccessToken, JsonSerializer.Serialize(metadata));
        Assert.DoesNotContain("BEGIN PRIVATE KEY", JsonSerializer.Serialize(metadata));
    }

    [Theory]
    [InlineData("all", "write", "", "AppScopeMintNotRestricted")]
    [InlineData("selected", "read", "", "AppScopeMintNotRestricted")]
    [InlineData("selected", "write", "administration", "AppScopeMintNotRestricted")]
    [InlineData("selected", "write", "wrongrepo", "AppScopeMintNotRestricted")]
    public async Task Invalid_permission_or_scope_response_is_rejected_before_followup_GET(
        string selection, string permission, string extra, string expected)
    {
        using var rsa = RSA.Create(2048);
        var calls = 0;
        using var client = new HttpClient(new Handler(_ =>
        {
            calls++;
            if (calls == 1)
                return Json(200, InstallationReply());
            if (calls != 2) throw new InvalidOperationException(
                "Unrestricted GitHub App token must never be used");
            return Json(201, MintReply(selection, permission, extra));
        }));
        var error = await Assert.ThrowsAsync<GitHubAppWriteScopeException>(() =>
            new GitHubAppScopedWriteCredentialInspector(
                client, TimeProvider.System).InspectAsync(
                1234, 77, rsa.ExportPkcs8PrivateKeyPem(),
                Owner, RepoName, default));
        Assert.Equal(expected, error.Code);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Refused_token_issuance_does_not_try_repository_write_or_GET()
    {
        using var rsa = RSA.Create(2048);
        var calls = 0;
        using var client = new HttpClient(new Handler(req =>
        {
            calls++;
            if (calls == 1)
            {
                Assert.Equal(HttpMethod.Get, req.Method);
                return Json(200, InstallationReply());
            }
            Assert.Equal(HttpMethod.Post, req.Method);
            return Json(403, "{}");
        }));
        var error = await Assert.ThrowsAsync<GitHubAppWriteScopeException>(() =>
            new GitHubAppScopedWriteCredentialInspector(
                client, TimeProvider.System).InspectAsync(
                1234, 77, rsa.ExportPkcs8PrivateKeyPem(),
                Owner, RepoName, default));
        Assert.Equal("AppScopeMintRejected", error.Code);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Archived_repo_on_second_GET_fails_even_if_permission_is_reported()
    {
        using var rsa = RSA.Create(2048);
        var calls = 0;
        using var client = new HttpClient(new Handler(req =>
        {
            calls++;
            return calls == 1
                ? Json(200, InstallationReply())
                : calls == 2 ? Json(201, MintReply())
                : Json(200, RepoReply().Replace(
                    "\"archived\":false", "\"archived\":true",
                    StringComparison.Ordinal));
        }));
        var error = await Assert.ThrowsAsync<GitHubAppWriteScopeException>(() =>
            new GitHubAppScopedWriteCredentialInspector(
                client, TimeProvider.System).InspectAsync(
                1234, 77, rsa.ExportPkcs8PrivateKeyPem(),
                Owner, RepoName, default));
        Assert.Equal("AppScopeRepositoryChanged", error.Code);
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData("root-token")]
    [InlineData("permission")]
    [InlineData("repository-id")]
    [InlineData("owner-login")]
    [InlineData("nested-owner-data")]
    public async Task Duplicate_claims_in_mint_response_are_rejected_before_token_use(
        string alteration)
    {
        using var rsa = RSA.Create(2048);
        var original = MintReply();
        var altered = alteration switch {
            "root-token" => original.Replace(
                "\"token\":", "\"token\":\"conflicting\",\"token\":",
                StringComparison.Ordinal),
            "permission" => original.Replace(
                "\"contents\":", "\"contents\":\"read\",\"contents\":",
                StringComparison.Ordinal),
            "repository-id" => original.Replace(
                "\"id\":2468", "\"id\":9999,\"id\":2468",
                StringComparison.Ordinal),
            "owner-login" => original.Replace(
                "\"login\":\"gh-org\"",
                "\"login\":\"untrusted\",\"login\":\"gh-org\"",
                StringComparison.Ordinal),
            "nested-owner-data" => original.Replace(
                "\"owner\":{", "\"owner\":{\"extra\":{\"a\":1,\"a\":2},",
                StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(alteration))
        };
        Assert.NotEqual(original, altered);
        using var parsed = JsonDocument.Parse(altered);
        Assert.Equal(JsonValueKind.Object, parsed.RootElement.ValueKind);
        var calls = 0;
        using var client = new HttpClient(new Handler(req =>
        {
            calls++;
            if (calls == 1)
            {
                Assert.Equal(HttpMethod.Get, req.Method);
                return Json(200, InstallationReply());
            }
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Equal(2, calls);
            return Json(201, altered);
        }));

        var error = await Assert.ThrowsAsync<GitHubAppWriteScopeException>(() =>
            new GitHubAppScopedWriteCredentialInspector(
                client, TimeProvider.System).InspectAsync(
                1234, 77, rsa.ExportPkcs8PrivateKeyPem(),
                Owner, RepoName, default));
        Assert.Equal("AppScopeMintNotRestricted", error.Code);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("archived")]
    [InlineData("owner-login")]
    [InlineData("nested-owner-data")]
    public async Task Duplicate_claims_in_token_bound_repository_GET_are_rejected(
        string alteration)
    {
        using var rsa = RSA.Create(2048);
        var original = RepoReply();
        var altered = alteration switch {
            "archived" => original.Replace(
                "\"archived\":false", "\"archived\":true,\"archived\":false",
                StringComparison.Ordinal),
            "owner-login" => original.Replace(
                "\"login\":\"gh-org\"",
                "\"login\":\"untrusted\",\"login\":\"gh-org\"",
                StringComparison.Ordinal),
            "nested-owner-data" => original.Replace(
                "\"owner\":{", "\"owner\":{\"extra\":{\"a\":1,\"a\":2},",
                StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(alteration))
        };
        Assert.NotEqual(original, altered);
        using var parsed = JsonDocument.Parse(altered);
        Assert.Equal(JsonValueKind.Object, parsed.RootElement.ValueKind);
        var calls = 0;
        using var client = new HttpClient(new Handler(req =>
        {
            calls++;
            if (calls == 1)
            {
                Assert.Equal(HttpMethod.Get, req.Method);
                return Json(200, InstallationReply());
            }
            if (calls == 2)
            {
                Assert.Equal(HttpMethod.Post, req.Method);
                return Json(201, MintReply());
            }
            Assert.Equal(3, calls);
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Equal(AccessToken, req.Headers.Authorization?.Parameter);
            return Json(200, altered);
        }));
        var error = await Assert.ThrowsAsync<GitHubAppWriteScopeException>(() =>
            new GitHubAppScopedWriteCredentialInspector(
                client, TimeProvider.System).InspectAsync(
                1234, 77, rsa.ExportPkcs8PrivateKeyPem(),
                Owner, RepoName, default));
        Assert.Equal("AppScopeRepositoryChanged", error.Code);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Bogus_private_key_fails_without_http_or_secret_exposure()
    {
        using var client = new HttpClient(new Handler(_ =>
            throw new InvalidOperationException("HTTP must not be reached")));
        var error = await Assert.ThrowsAsync<GitHubAppWriteScopeException>(() =>
            new GitHubAppScopedWriteCredentialInspector(
                client, TimeProvider.System).InspectAsync(
                1234, 77, "not-a-pem", Owner, RepoName, default));
        Assert.Equal("AppScopeSigningKeyInvalid", error.Code);
    }

    private const string Sha =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static string MintReply(
        string selection = "selected", string permission = "write",
        string extra = "")
    {
        var perms = new Dictionary<string, string> { ["contents"] = permission,
            ["metadata"] = "read" };
        if (extra == "administration") perms["administration"] = "write";
        return JsonSerializer.Serialize(new {
            token = AccessToken,
            expires_at = DateTimeOffset.UtcNow.AddMinutes(45).ToString("O"),
            repository_selection = selection,
            permissions = perms,
            repositories = new[] {
                new { id = 2468,
                    full_name = extra == "wrongrepo" ? "gh-org/other" :
                        Owner + "/" + RepoName,
                    owner = new { login = Owner } }
            }
        });
    }

    private static string InstallationReply() => JsonSerializer.Serialize(new {
        id = 77L, app_id = 1234L, target_id = 9876L,
        target_type = "Organization", repository_selection = "selected",
        suspended_at = (string?)null,
        account = new { id = 9876L, login = Owner,
            type = "Organization" },
        permissions = new { contents = "write", metadata = "read" }
    });

    private static string RepoReply() => JsonSerializer.Serialize(new {
        id = 2468, full_name = Owner + "/" + RepoName,
        owner = new { login = Owner }, archived = false, disabled = false
    });

    private static byte[] DecodePart(string part)
    {
        var base64 = part.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight(
            (base64.Length + 3) / 4 * 4, '='));
    }

    private static HttpResponseMessage Json(int code, string body) =>
        new((HttpStatusCode)code) {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> cb)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(cb(req));
    }
}
