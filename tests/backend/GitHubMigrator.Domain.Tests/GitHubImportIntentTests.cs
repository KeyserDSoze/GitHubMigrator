using System.Net;
using System.Text;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.GitHub;
using GitHubMigrator.Infrastructure.PostgreSql;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubImportIntentTests
{
    [Fact]
    public void Safe_plan_only_creates_missing_refs_and_preserves_target_only_refs()
    {
        var items = new[] {
            new GitMirrorRefComparison("refs/heads/main", One, One,
                GitMirrorRefDisposition.MatchingObjectId, "None"),
            new GitMirrorRefComparison("refs/heads/new", Two, null,
                GitMirrorRefDisposition.CreateCandidate, "DestinationWritePermissionUnverified"),
            new GitMirrorRefComparison("refs/tags/target-only", null, Three,
                GitMirrorRefDisposition.TargetOnlyPreserved, "TargetRefDeletionProhibited")
        };
        var id = Guid.NewGuid();
        var first = GitHubSafeRefPlanner.Build(id, Digest, Digest, Digest, items);
        var again = GitHubSafeRefPlanner.Build(id, Digest, Digest, Digest, items);
        Assert.Equal(first.EvidenceSha256, again.EvidenceSha256);
        Assert.Equal(1, first.CreateCandidates);
        Assert.Equal(1, first.AlreadyMatching);
        Assert.Equal(0, first.DivergedBlocked);
        Assert.Equal(1, first.TargetOnlyPreserved);
        Assert.True(first.HasOnlyNonDestructiveCandidates);
        Assert.NotEqual(first.EvidenceSha256,
            GitHubSafeRefPlanner.Build(id, Digest, Digest, new string('b', 64), items).EvidenceSha256);
    }

    [Fact]
    public void Divergent_ref_is_always_blocked_regardless_of_change_ancestry()
    {
        var first = GitHubSafeRefPlanner.Build(Guid.NewGuid(), Digest, Digest, Digest,
            [new("refs/heads/main", One, Two,
                GitMirrorRefDisposition.DivergedBlocked, "RefFastForwardUnverified")]);
        Assert.False(first.HasOnlyNonDestructiveCandidates);
        Assert.Equal(1, first.DivergedBlocked);
        Assert.Equal(0, first.CreateCandidates);
    }

    [Fact]
    public void Malformed_duplicate_or_misrepresented_ref_fails_closed()
    {
        var duplicate = new[] {
            new GitMirrorRefComparison("refs/heads/main", One, null,
                GitMirrorRefDisposition.CreateCandidate, ""),
            new GitMirrorRefComparison("refs/heads/main", Two, null,
                GitMirrorRefDisposition.CreateCandidate, "")
        };
        Assert.Equal("ImportIntentRefPlanInvalid",
            Assert.Throws<GitHubImportIntentException>(() =>
                GitHubSafeRefPlanner.Build(Guid.NewGuid(), Digest, Digest, Digest, duplicate)).Code);
        Assert.Equal("ImportIntentRefPlanInvalid",
            Assert.Throws<GitHubImportIntentException>(() =>
                GitHubSafeRefPlanner.Build(Guid.NewGuid(), Digest, Digest, Digest,
                    [new("refs/heads/main", One, Two,
                        GitMirrorRefDisposition.CreateCandidate, "")])).Code);
    }

    [Fact]
    public void Intent_evidence_digest_binds_actor_policy_bundle_and_operations()
    {
        var draft = Draft();
        var sha = GitHubImportIntentIntegrity.Seal(draft);
        Assert.Equal(64, sha.Length);
        Assert.Equal(sha, GitHubImportIntentIntegrity.Seal(draft));
        Assert.NotEqual(sha, GitHubImportIntentIntegrity.Seal(
            draft with { RequestedBy = Guid.NewGuid() }));
        Assert.NotEqual(sha, GitHubImportIntentIntegrity.Seal(
            draft with { BundleSha256 = new string('a', 64) }));
        Assert.NotEqual(sha, GitHubImportIntentIntegrity.Seal(
            draft with { Policy = draft.Policy with { ActiveRulesets = 1, RulesetCount = 1 } }));
        Assert.Equal("ImportIntentIntegrityInvalid",
            Assert.Throws<GitHubImportIntentException>(() =>
                GitHubImportIntentIntegrity.Seal(draft with {
                    Plan = draft.Plan with { DivergedBlocked = 1 }
                })).Code);
    }

    [Fact]
    public async Task Fixed_GitHub_policy_GET_only_classifies_rulesets_and_branch_protection()
    {
        var paths = new List<string>();
        using var client = new HttpClient(new Handler(req =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Equal("api.github.com", req.RequestUri!.Host);
            Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
            Assert.Equal("private-token", req.Headers.Authorization?.Parameter);
            paths.Add(req.RequestUri.PathAndQuery);
            return req.RequestUri.AbsolutePath switch
            {
                "/repos/gh-org/destination" => Json(200, """
                    {"full_name":"gh-org/destination","owner":{"login":"gh-org"},
                     "default_branch":"main"}
                    """),
                "/repos/gh-org/destination/rulesets" => Json(200, """
                    [{"id":3,"enforcement":"active"},
                     {"id":7,"enforcement":"disabled"}]
                    """),
                "/repos/gh-org/destination/branches/main/protection" =>
                    Json(200, "{}"),
                _ => throw new InvalidOperationException("Unexpected API request")
            };
        }));
        var observed = await new GitHubImportPolicyReader(client, TimeProvider.System)
            .InspectAsync("gh-org", "destination", "private-token", default);
        Assert.Equal("Observed", observed.RulesetStatus);
        Assert.Equal(2, observed.RulesetCount);
        Assert.Equal(1, observed.ActiveRulesets);
        Assert.Equal("ProtectionVisible", observed.DefaultBranchProtection);
        Assert.Equal(3, paths.Count);
        Assert.Contains("/repos/gh-org/destination/rulesets?includes_parents=true&per_page=100", paths);
        Assert.DoesNotContain(paths, p => p.Contains("private-token", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(401, "Unavailable")]
    [InlineData(403, "Unavailable")]
    [InlineData(404, "Unavailable")]
    [InlineData(429, "Unavailable")]
    public async Task Missing_or_unavailable_repository_never_proves_absence_of_rules(
        int status, string expected)
    {
        var count = 0;
        using var client = new HttpClient(new Handler(_ =>
        {
            count++;
            return Json(status, "{}");
        }));
        var observed = await new GitHubImportPolicyReader(client, TimeProvider.System)
            .InspectAsync("gh-org", "dest", "pat", default);
        Assert.Equal(expected, observed.RulesetStatus);
        Assert.Equal("Unverified", observed.DefaultBranchProtection);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Paginated_or_ambiguous_rulesets_are_never_declared_complete()
    {
        using var client = new HttpClient(new Handler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/repos/gh-org/dest")
                return Json(200, """
                    {"full_name":"gh-org/dest","owner":{"login":"gh-org"},"default_branch":"main"}
                    """);
            var response = Json(200, "[]");
            response.Headers.TryAddWithoutValidation("Link",
                "<https://api.github.com/repos/gh-org/dest/rulesets?page=2>; rel=\"next\"");
            return response;
        }));
        var state = await new GitHubImportPolicyReader(client, TimeProvider.System)
            .InspectAsync("gh-org", "dest", "pat", default);
        Assert.Equal("Incomplete", state.RulesetStatus);
        Assert.Equal("Unverified", state.DefaultBranchProtection);
    }

    [Fact]
    public async Task Branch_protection_404_is_ambiguous_not_permission_verified()
    {
        using var client = new HttpClient(new Handler(req =>
            req.RequestUri!.AbsolutePath switch {
                "/repos/gh-org/dest" => Json(200, """
                    {"full_name":"gh-org/dest","owner":{"login":"gh-org"},"default_branch":"main"}
                    """),
                "/repos/gh-org/dest/rulesets" => Json(200, "[]"),
                _ => Json(404, "{}")
            }));
        var state = await new GitHubImportPolicyReader(client, TimeProvider.System)
            .InspectAsync("gh-org", "dest", "pat", default);
        Assert.Equal("Observed", state.RulesetStatus);
        Assert.Equal("NotVisibleOrMissing", state.DefaultBranchProtection);
    }

    private static GitHubImportIntentDraft Draft()
    {
        var plan = new GitHubSafeRefPlan(new string('a', 64), 1, 0, 0, 0, true);
        return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Digest, Digest, Digest, Digest, plan,
            new("Observed", 0, 0, "NotVisibleOrMissing",
                DateTimeOffset.UtcNow), Guid.NewGuid());
    }

    private const string Digest = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string One = "1111111111111111111111111111111111111111";
    private const string Two = "2222222222222222222222222222222222222222";
    private const string Three = "3333333333333333333333333333333333333333";

    private static HttpResponseMessage Json(int status, string json) =>
        new((HttpStatusCode)status) {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> callback)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(callback(request));
    }
}
