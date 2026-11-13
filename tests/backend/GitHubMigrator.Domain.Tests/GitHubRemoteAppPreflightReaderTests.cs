using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.GitHub;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubRemoteAppPreflightReaderTests
{
    private const string Owner = "gh-org";
    private const string Repo = "sandbox";
    private const string Token = "ghs_PRIVATE_F051_SCOPED_TOKEN_NEVER_RETURNED";
    private static readonly string MainOid = new('a', 40);
    private static readonly string CandidateOid = new('b', 40);
    private static readonly string AnnotatedTagOid = new('c', 40);

    [Fact]
    public async Task Exactly_one_repo_App_token_is_minted_and_consumed_by_fixed_host_GET_only()
    {
        using var rsa = RSA.Create(2048);
        var source = Packet();
        var requests = new List<string>();
        using var client = new HttpClient(new Handler(req =>
        {
            Assert.Equal("api.github.com", req.RequestUri!.Host);
            Assert.Equal("https", req.RequestUri.Scheme);
            requests.Add(req.Method + " " + req.RequestUri.AbsolutePath);
            var path = req.RequestUri.AbsolutePath;
            if (path == "/app/installations/77")
            {
                Assert.Equal(HttpMethod.Get, req.Method);
                Assert.NotEqual(Token, req.Headers.Authorization?.Parameter);
                return Reply(200, Installation());
            }
            if (path.EndsWith("/access_tokens", StringComparison.Ordinal))
            {
                Assert.Equal(HttpMethod.Post, req.Method);
                Assert.Equal("/app/installations/77/access_tokens", path);
                Assert.NotEqual(Token, req.Headers.Authorization?.Parameter);
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(body);
                Assert.Equal(Repo, doc.RootElement.GetProperty("repositories")[0]
                    .GetString());
                Assert.Equal("write", doc.RootElement.GetProperty("permissions")
                    .GetProperty("contents").GetString());
                Assert.Single(doc.RootElement.GetProperty("permissions")
                    .EnumerateObject());
                return Reply(201, Mint());
            }
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Equal(Token, req.Headers.Authorization?.Parameter);
            if (path.EndsWith("/git/matching-refs/heads", StringComparison.Ordinal))
                return Reply(200, JsonSerializer.Serialize(new[] {
                    new { @ref = "refs/heads/main", @object =
                        new { sha = MainOid, type = "commit" } }
                }));
            if (path.EndsWith("/git/matching-refs/tags", StringComparison.Ordinal))
                return Reply(200, JsonSerializer.Serialize(new[] {
                    new { @ref = "refs/tags/v1", @object =
                        new { sha = AnnotatedTagOid, type = "tag" } }
                }));
            if (path.EndsWith("/rulesets", StringComparison.Ordinal))
                return Reply(200, "[]");
            if (path.Contains("/rules/branches/", StringComparison.Ordinal))
                return Reply(200, "[]");
            if (path == "/repos/gh-org/sandbox")
                return Reply(200, Metadata());
            throw new InvalidOperationException("Unexpected GitHub endpoint");
        }));
        var reader = new GitHubAppScopedWriteCredentialInspector(
            client, TimeProvider.System);
        var actual = await reader.InspectAsync(
            1234, 77, rsa.ExportPkcs8PrivateKeyPem(), source, default);

        Assert.Equal(77, actual.InstallationId);
        Assert.Equal(2468, actual.RepositoryId);
        Assert.Equal(2, actual.TargetRefs.Refs.Count);
        Assert.Contains(actual.TargetRefs.Refs, x =>
            x.Name == "refs/tags/v1" &&
            x.ObjectType == "tag" && x.ObjectId == AnnotatedTagOid);
        Assert.Equal(1, actual.CandidatePolicies.Items.Count);
        Assert.All(actual.CandidatePolicies.Items, x =>
            Assert.Equal(GitHubCandidatePolicyStates.NoActiveRulesReported,
                x.Status));
        Assert.Equal(11, requests.Count);
        Assert.Equal(2, requests.Count(x =>
            x == "GET /repos/gh-org/sandbox/git/matching-refs/heads"));
        Assert.Equal(2, requests.Count(x =>
            x == "GET /repos/gh-org/sandbox/git/matching-refs/tags"));
        Assert.Equal(3, requests.Count(x =>
            x == "GET /repos/gh-org/sandbox"));
        Assert.Equal("GET /repos/gh-org/sandbox", requests[^1]);
        Assert.Single(requests.Where(x => x.StartsWith("POST ")));
        Assert.Contains("GET /app/installations/77", requests);
        Assert.All(requests.Where(x =>
            x.StartsWith("GET /repos/", StringComparison.Ordinal)), x =>
            Assert.StartsWith("GET /repos/gh-org/sandbox", x));
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(actual));
    }

    [Theory]
    [InlineData("head")]
    [InlineData("annotated-tag")]
    public async Task F073_ref_changed_during_policy_GET_cannot_seal_preflight(
        string changed)
    {
        using var rsa = RSA.Create(2048);
        var heads = 0;
        var tags = 0;
        var repositories = 0;
        var tokenMints = 0;
        using var client = new HttpClient(new Handler(req =>
        {
            Assert.Equal("api.github.com", req.RequestUri!.Host);
            var path = req.RequestUri.AbsolutePath;
            if (path == "/app/installations/77")
            {
                Assert.Equal(HttpMethod.Get, req.Method);
                return Reply(200, Installation());
            }
            if (path.EndsWith("/access_tokens", StringComparison.Ordinal))
            {
                Assert.Equal(HttpMethod.Post, req.Method);
                tokenMints++;
                return Reply(201, Mint());
            }
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Equal(Token, req.Headers.Authorization?.Parameter);
            if (path == "/repos/gh-org/sandbox")
            {
                repositories++;
                return Reply(200, Metadata());
            }
            if (path.EndsWith("/git/matching-refs/heads",
                StringComparison.Ordinal))
            {
                heads++;
                return Reply(200, JsonSerializer.Serialize(new[] {
                    new { @ref = "refs/heads/main", @object = new {
                        sha = heads == 2 && changed == "head"
                            ? new string('d', 40) : MainOid,
                        type = "commit"
                    }}
                }));
            }
            if (path.EndsWith("/git/matching-refs/tags",
                StringComparison.Ordinal))
            {
                tags++;
                return Reply(200, JsonSerializer.Serialize(new[] {
                    new { @ref = "refs/tags/v1", @object = new {
                        sha = tags == 2 && changed == "annotated-tag"
                            ? new string('d', 40) : AnnotatedTagOid,
                        type = "tag"
                    }}
                }));
            }
            if (path.EndsWith("/rulesets", StringComparison.Ordinal) ||
                path.Contains("/rules/branches/", StringComparison.Ordinal))
                return Reply(200, "[]");
            throw new InvalidOperationException("Unexpected GitHub endpoint");
        }));

        var ex = await Assert.ThrowsAsync<GitHubRemoteAppPreflightException>(
            () => new GitHubAppScopedWriteCredentialInspector(
                client, TimeProvider.System).InspectAsync(
                    1234, 77, rsa.ExportPkcs8PrivateKeyPem(),
                    Packet(), default));
        Assert.Equal("RemotePreflightRefsChangedDuringReview", ex.Code);
        Assert.Equal(1, tokenMints);
        Assert.Equal(2, heads);
        Assert.Equal(2, tags);
        Assert.Equal(2, repositories);
        Assert.DoesNotContain(Token, ex.Message);
    }

    [Theory]
    [InlineData("repository-id")]
    [InlineData("owner")]
    [InlineData("archived")]
    [InlineData("disabled")]
    [InlineData("duplicate-identity")]
    [InlineData("hidden")]
    public async Task F073_repository_changed_at_final_GET_fails_closed(
        string mutation)
    {
        using var rsa = RSA.Create(2048);
        var repositories = 0;
        var heads = 0;
        var tags = 0;
        var mints = 0;
        using var client = new HttpClient(new Handler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/app/installations/77")
                return Reply(200, Installation());
            if (path.EndsWith("/access_tokens", StringComparison.Ordinal))
            {
                Assert.Equal(HttpMethod.Post, req.Method);
                mints++;
                return Reply(201, Mint());
            }
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Equal(Token, req.Headers.Authorization?.Parameter);
            if (path == "/repos/gh-org/sandbox")
            {
                repositories++;
                var stable = Metadata();
                if (repositories < 3)
                    return Reply(200, stable);
                var altered = mutation switch
                {
                    "repository-id" => Metadata(9999),
                    "owner" => stable.Replace(
                        "\"login\":\"gh-org\"",
                        "\"login\":\"other-org\"",
                        StringComparison.Ordinal),
                    "archived" => stable.Replace(
                        "\"archived\":false", "\"archived\":true",
                        StringComparison.Ordinal),
                    "disabled" => stable.Replace(
                        "\"disabled\":false", "\"disabled\":true",
                        StringComparison.Ordinal),
                    "duplicate-identity" => stable.Replace(
                        "\"id\":2468",
                        "\"id\":1111,\"id\":2468",
                        StringComparison.Ordinal),
                    "hidden" => "{}",
                    _ => throw new ArgumentOutOfRangeException(nameof(mutation))
                };
                return Reply(mutation == "hidden" ? 404 : 200, altered);
            }
            if (path.EndsWith("/git/matching-refs/heads",
                StringComparison.Ordinal))
            {
                heads++;
                return Reply(200, JsonSerializer.Serialize(new[] {
                    new { @ref = "refs/heads/main", @object = new {
                        sha = MainOid, type = "commit"
                    }}
                }));
            }
            if (path.EndsWith("/git/matching-refs/tags",
                StringComparison.Ordinal))
            {
                tags++;
                return Reply(200, JsonSerializer.Serialize(new[] {
                    new { @ref = "refs/tags/v1", @object = new {
                        sha = AnnotatedTagOid, type = "tag"
                    }}
                }));
            }
            if (path.EndsWith("/rulesets", StringComparison.Ordinal) ||
                path.Contains("/rules/branches/", StringComparison.Ordinal))
                return Reply(200, "[]");
            throw new InvalidOperationException("Unexpected GitHub endpoint");
        }));

        var ex = await Assert.ThrowsAsync<GitHubRemoteAppPreflightException>(
            () => new GitHubAppScopedWriteCredentialInspector(
                client, TimeProvider.System).InspectAsync(
                    1234, 77, rsa.ExportPkcs8PrivateKeyPem(),
                    Packet(), default));
        Assert.Equal("RemotePreflightRepositoryChangedDuringReview", ex.Code);
        Assert.Equal(1, mints);
        Assert.Equal(3, repositories);
        Assert.Equal(2, heads);
        Assert.Equal(2, tags);
        Assert.DoesNotContain(Token, ex.Message);
    }

    [Theory]
    [InlineData("all", "write", "AppScopeMintNotRestricted")]
    [InlineData("selected", "read", "AppScopeMintNotRestricted")]
    [InlineData("selected", "admin", "AppScopeMintNotRestricted")]
    public async Task Incorrect_App_scope_refuses_before_even_the_first_GET(
        string selection, string permission, string expected)
    {
        using var rsa = RSA.Create(2048);
        var calls = 0;
        using var client = new HttpClient(new Handler(req =>
        {
            calls++;
            if (req.Method == HttpMethod.Get)
            {
                Assert.Equal("/app/installations/77",
                    req.RequestUri!.AbsolutePath);
                return Reply(200, Installation());
            }
            Assert.Equal(HttpMethod.Post, req.Method);
            return Reply(201, Mint(selection, permission));
        }));
        var ex = await Assert.ThrowsAsync<GitHubAppWriteScopeException>(
            () => new GitHubAppScopedWriteCredentialInspector(
                client, TimeProvider.System).InspectAsync(
                    1234, 77, rsa.ExportPkcs8PrivateKeyPem(),
                    Packet(), default));
        Assert.Equal(expected, ex.Code);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Unknown_or_hidden_remote_refs_do_not_become_empty_target()
    {
        using var rsa = RSA.Create(2048);
        using var client = new HttpClient(new Handler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/app/installations/77")
                return Reply(200, Installation());
            if (path.EndsWith("/access_tokens")) return Reply(201, Mint());
            if (path == "/repos/gh-org/sandbox") return Reply(200, Metadata());
            Assert.Equal(HttpMethod.Get, req.Method);
            return Reply(404, "{}");
        }));
        var ex = await Assert.ThrowsAsync<GitHubRefObservationException>(
            () => new GitHubAppScopedWriteCredentialInspector(
                client, TimeProvider.System).InspectAsync(
                    1234, 77, rsa.ExportPkcs8PrivateKeyPem(),
                    Packet(), default));
        Assert.Equal("TargetRepositoryNotVisible", ex.Code);
    }

    [Fact]
    public async Task Mismatched_numeric_repository_identity_refuses_before_ref_GET()
    {
        using var rsa = RSA.Create(2048);
        var calls = 0;
        using var client = new HttpClient(new Handler(req =>
        {
            calls++;
            if (calls == 1) return Reply(200, Installation());
            if (calls == 2) return Reply(201, Mint());
            Assert.Equal(HttpMethod.Get, req.Method);
            return Reply(200, Metadata(9999));
        }));
        var ex = await Assert.ThrowsAsync<GitHubAppWriteScopeException>(
            () => new GitHubAppScopedWriteCredentialInspector(
                client, TimeProvider.System).InspectAsync(
                    1234, 77, rsa.ExportPkcs8PrivateKeyPem(),
                    Packet(), default));
        Assert.Equal("AppScopeRepositoryChanged", ex.Code);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Packet_with_fake_candidate_digest_never_mints_an_App_token()
    {
        using var rsa = RSA.Create(2048);
        var calls = 0;
        using var client = new HttpClient(new Handler(_ =>
        {
            calls++; return Reply(200, "{}");
        }));
        var source = Packet();
        var fake = source with {
            Packet = source.Packet with {
                CandidateSetSha256 = new string('f', 64)
            }
        };
        var ex = await Assert.ThrowsAsync<GitHubRemoteAppPreflightException>(
            () => new GitHubAppScopedWriteCredentialInspector(
                client, TimeProvider.System).InspectAsync(
                    1234, 77, rsa.ExportPkcs8PrivateKeyPem(),
                    fake, default));
        Assert.Equal("RemotePreflightPacketInvalid", ex.Code);
        Assert.Equal(0, calls);
    }

    private static GitHubCreateOnlyCandidatePacket Packet()
    {
        var candidate = new GitMirrorRef(
            "refs/heads/feature", CandidateOid);
        var digest = GitImportRehearsalIntegrity.SealRefs(
            "f044-create-only-candidate-refs-v1", [candidate]);
        return new(new(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            2468, Owner, Repo, Sha('1'), Sha('2'), Sha('3'),
            Sha('4'), Sha('5'), Sha('6'), Sha('7'), Sha('8'),
            Sha('9'), digest, Sha('b'), 1, 1, 1,
            GitHubCreateOnlyPacketIntegrity.State,
            false, false, false, false), [candidate]);
    }

    private static string Sha(char c) => new(c, 64);

    private static string Mint(string selection = "selected",
        string permission = "write") => JsonSerializer.Serialize(new {
            token = Token,
            expires_at = DateTimeOffset.UtcNow.AddMinutes(45).ToString("O"),
            repository_selection = selection,
            permissions = new Dictionary<string,string> {
                ["contents"] = permission, ["metadata"] = "read"
            },
            repositories = new[] {
                new { id = 2468, full_name = Owner + "/" + Repo,
                    owner = new { login = Owner } }
            }
        });

    private static string Installation() => JsonSerializer.Serialize(new {
        id = 77L, app_id = 1234L, target_id = 9876L,
        target_type = "Organization", repository_selection = "selected",
        suspended_at = (string?)null,
        account = new { id = 9876L, login = Owner,
            type = "Organization" },
        permissions = new { contents = "write", metadata = "read" }
    });

    private static string Metadata(long id = 2468) => JsonSerializer.Serialize(
        new { id, full_name = Owner + "/" + Repo,
            owner = new { login = Owner },
            archived = false, disabled = false });

    private static HttpResponseMessage Reply(int code, string json) =>
        new((HttpStatusCode)code) {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(handle(request));
    }
}
