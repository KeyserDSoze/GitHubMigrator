using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.GitHub;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubCandidatePolicyReaderTests
{
    private const string Oid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Token = "READ_ONLY_SERVER_SECRET_NOT_FOR_RESPONSES";
    private static readonly DateTimeOffset Now =
        new(2026, 11, 6, 11, 40, 0, TimeSpan.Zero);
    private static readonly GitMirrorRef[] Candidates = [
        new("refs/heads/new-branch", Oid),
        new("refs/tags/release", Oid)
    ];

    [Fact]
    public async Task Only_fixed_host_GETs_observe_absent_branch_rules_and_tag_rulesets()
    {
        var calls = new List<string>();
        using var http = Client(req =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Equal("api.github.com", req.RequestUri!.Host);
            Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
            Assert.Equal(Token, req.Headers.Authorization?.Parameter);
            calls.Add(req.RequestUri.PathAndQuery);
            return req.RequestUri.AbsolutePath switch {
                "/repos/gh-org/sandbox" => Json(200, Repository()),
                "/repos/gh-org/sandbox/rulesets" => Json(200, "[]"),
                "/repos/gh-org/sandbox/rules/branches/new-branch" =>
                    Json(200, "[]"),
                _ => throw new Exception("Unexpected route")
            };
        });
        var observed = await Reader(http).ReadAsync(
            "gh-org", "sandbox", 2468, Token, Candidates, default);
        Assert.Equal(3, calls.Count);
        Assert.Contains("/repos/gh-org/sandbox/rulesets?includes_parents=true&per_page=100", calls);
        Assert.Contains("/repos/gh-org/sandbox/rules/branches/new-branch?per_page=100", calls);
        Assert.All(observed.Items, x => {
            Assert.Equal(GitHubCandidatePolicyStates.NoActiveRulesReported, x.Status);
            Assert.Equal("Observed", x.ProviderStatus);
            Assert.Equal(0, x.ActiveRules);
            Assert.DoesNotContain("refs/", x.RefNameSha256);
        });
        var sealedReview = GitHubCandidatePolicyIntegrity.Seal(
            CandidatePacket(Candidates), observed);
        Assert.Equal(2, sealedReview.NoActiveRulesReported);
        Assert.Equal("AdvisoryNoRulesReported", sealedReview.Status);
        Assert.Equal(64, sealedReview.EvidenceSha256.Length);
        Assert.False(sealedReview.WriteCapabilityVerified);
        Assert.False(sealedReview.ExecutionAuthorized);
        Assert.False(sealedReview.RemoteWritesPerformed);
        Assert.False(sealedReview.ReadyForExecution);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(sealedReview));
        Assert.DoesNotContain("new-branch", JsonSerializer.Serialize(sealedReview));
    }

    [Fact]
    public async Task Inherited_push_and_tag_rules_always_force_manual_review()
    {
        var catalog = """
            [{"target":"push","enforcement":"active"},
             {"target":"tag","enforcement":"active"},
             {"target":"branch","enforcement":"evaluate"}]
            """;
        using var client = Client(req => req.RequestUri!.AbsolutePath switch {
            "/repos/gh-org/sandbox" => Json(200, Repository()),
            "/repos/gh-org/sandbox/rulesets" => Json(200, catalog),
            "/repos/gh-org/sandbox/rules/branches/new-branch" => Json(200, "[]"),
            _ => throw new InvalidOperationException()
        });
        var observed = await Reader(client).ReadAsync(
            "gh-org", "sandbox", 2468, Token, Candidates, default);
        Assert.All(observed.Items, x =>
            Assert.Equal(GitHubCandidatePolicyStates.RulesRequireReview, x.Status));
        Assert.Equal(2, GitHubCandidatePolicyIntegrity
            .Seal(CandidatePacket(Candidates), observed).RulesRequireReview);
    }

    [Fact]
    public async Task Effective_branch_rules_are_requested_even_for_nonexistent_names()
    {
        using var client = Client(req => req.RequestUri!.AbsolutePath switch {
            "/repos/gh-org/sandbox" => Json(200, Repository()),
            "/repos/gh-org/sandbox/rulesets" => Json(200, "[]"),
            "/repos/gh-org/sandbox/rules/branches/new-branch" =>
                Json(200, """[{"type":"creation"}]"""),
            _ => throw new InvalidOperationException()
        });
        var observed = await Reader(client).ReadAsync(
            "gh-org", "sandbox", 2468, Token, Candidates, default);
        Assert.Equal(GitHubCandidatePolicyStates.RulesRequireReview,
            observed.Items[0].Status);
        Assert.Equal(GitHubCandidatePolicyStates.NoActiveRulesReported,
            observed.Items[1].Status);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task Repository_error_or_redirect_never_mints_false_policy_clearance(
        int responseCode)
    {
        var count = 0;
        using var http = Client(_ =>
        {
            count++;
            return Json(responseCode, Repository());
        });
        var result = await Reader(http).ReadAsync(
            "gh-org", "sandbox", 2468, Token, Candidates, default);
        Assert.Equal(1, count);
        Assert.All(result.Items, x =>
            Assert.Equal(GitHubCandidatePolicyStates.PolicyUnverified, x.Status));
        Assert.Equal("RepoUnverified", result.RulesetCatalogStatus);
    }

    [Fact]
    public async Task Numeric_repository_ID_mismatch_is_not_authorized()
    {
        using var http = Client(_ => Json(200, Repository()));
        var result = await Reader(http).ReadAsync(
            "gh-org", "sandbox", 7777, Token, Candidates, default);
        Assert.All(result.Items, x =>
            Assert.Equal(GitHubCandidatePolicyStates.PolicyUnverified, x.Status));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("paginated")]
    [InlineData("oversize")]
    [InlineData("invalid-target")]
    [InlineData("missing-enforcement")]
    public async Task Untrusted_or_incomplete_ruleset_catalog_fails_closed(
        string type)
    {
        var calls = 0;
        using var http = Client(req =>
        {
            calls++;
            if (req.RequestUri!.AbsolutePath == "/repos/gh-org/sandbox")
                return Json(200, Repository());
            var response = type switch {
                "duplicate" => Json(200,
                    """[{"target":"tag","target":"push","enforcement":"active"}]"""),
                "oversize" => Json(200, new string(' ', 65_537)),
                "invalid-target" => Json(200,
                    """[{"target":"repository","enforcement":"active"}]"""),
                "missing-enforcement" => Json(200, """[{"target":"tag"}]"""),
                _ => Json(200, "[]")
            };
            if (type == "paginated")
                response.Headers.TryAddWithoutValidation("Link",
                    """<https://api.github.com/repos/gh-org/sandbox/rulesets?page=2>; rel="next" """);
            return response;
        });
        var result = await Reader(http).ReadAsync(
            "gh-org", "sandbox", 2468, Token, Candidates, default);
        Assert.Equal(2, calls);
        Assert.Equal("CatalogUnverified", result.RulesetCatalogStatus);
        Assert.All(result.Items, x =>
            Assert.Equal(GitHubCandidatePolicyStates.PolicyUnverified, x.Status));
    }

    [Theory]
    [InlineData(403, "[]")]
    [InlineData(404, "[]")]
    [InlineData(429, "[]")]
    [InlineData(200, """[{"type":"creation","type":"non_fast_forward"}]""")]
    [InlineData(200, """[{"unexpected":"no-type"}]""")]
    public async Task Branch_rules_unavailable_or_ambiguous_never_masquerade_as_no_rules(
        int code, string response)
    {
        using var http = Client(req => req.RequestUri!.AbsolutePath switch {
            "/repos/gh-org/sandbox" => Json(200, Repository()),
            "/repos/gh-org/sandbox/rulesets" => Json(200, "[]"),
            "/repos/gh-org/sandbox/rules/branches/new-branch" =>
                Json(code, response),
            _ => throw new InvalidOperationException()
        });
        var result = await Reader(http).ReadAsync(
            "gh-org", "sandbox", 2468, Token, Candidates, default);
        Assert.Equal(GitHubCandidatePolicyStates.PolicyUnverified,
            result.Items[0].Status);
        Assert.Equal(GitHubCandidatePolicyStates.NoActiveRulesReported,
            result.Items[1].Status);
    }

    [Fact]
    public async Task Malformed_paths_or_large_batch_reject_before_any_http()
    {
        var count = 0;
        using var http = Client(_ => { count++; return Json(200, "[]"); });
        var reader = Reader(http);
        await Assert.ThrowsAsync<GitHubCandidatePolicyException>(() =>
            reader.ReadAsync("gh-org", "../admin", 2468,
                Token, Candidates, default));
        await Assert.ThrowsAsync<GitHubCandidatePolicyException>(() =>
            reader.ReadAsync("gh-org", "sandbox", 2468,
                Token, Enumerable.Range(0, 17)
                    .Select(x => new GitMirrorRef($"refs/heads/test-{x:00}", Oid))
                    .ToArray(), default));
        Assert.Equal(0, count);
    }

    [Fact]
    public void Forged_order_counters_or_remote_approval_cannot_be_sealed()
    {
        var source = CandidatePacket(Candidates);
        var observation = new GitHubCandidatePolicyObservation([
            new(GitHubCandidatePolicyIntegrity.RefNameDigest(Candidates[0].Name),
                "Branch", GitHubCandidatePolicyStates.NoActiveRulesReported,
                0, "Observed"),
            new(GitHubCandidatePolicyIntegrity.RefNameDigest(Candidates[1].Name),
                "Tag", GitHubCandidatePolicyStates.PolicyUnverified,
                0, "TagUnverified")
        ], Now, "Observed");
        var review = GitHubCandidatePolicyIntegrity.Seal(source, observation);
        Assert.Equal(1, review.PolicyUnverified);
        Assert.Equal("HumanReviewRequired", review.Status);
        Assert.Throws<GitHubCandidatePolicyException>(() =>
            GitHubCandidatePolicyIntegrity.Seal(source,
                observation with { Items = observation.Items.Reverse().ToArray() }));
        Assert.Throws<GitHubCandidatePolicyException>(() =>
            GitHubCandidatePolicyIntegrity.Seal(source,
                observation with { Items = [
                    observation.Items[0] with { ActiveRules = 1 },
                    observation.Items[1] ] }));
        Assert.Throws<GitHubCandidatePolicyException>(() =>
            GitHubCandidatePolicyIntegrity.Seal(source, observation with {
                Items = [
                    observation.Items[0] with {
                        ProviderStatus = "BranchUnverified" },
                    observation.Items[1] ]
            }));
        Assert.Throws<GitHubCandidatePolicyException>(() =>
            GitHubCandidatePolicyIntegrity.Seal(source, observation with {
                RulesetCatalogStatus = "CatalogUnverified"
            }));
        Assert.Throws<GitHubCandidatePolicyException>(() =>
            GitHubCandidatePolicyIntegrity.Seal(
                source with { Packet = source.Packet with {
                    ExecutionAuthorized = true } }, observation));
    }

    [Theory]
    [InlineData(GitHubRemoteWriteSignal.NotStarted, false)]
    [InlineData(GitHubRemoteWriteSignal.RejectedBeforeTransmit, false)]
    [InlineData(GitHubRemoteWriteSignal.TimeoutAfterTransmit, true)]
    [InlineData(GitHubRemoteWriteSignal.ConnectionLost, true)]
    [InlineData(GitHubRemoteWriteSignal.ServerErrorAfterTransmit, true)]
    [InlineData(GitHubRemoteWriteSignal.ReportedPartialSuccess, true)]
    [InlineData(GitHubRemoteWriteSignal.ReportedSuccess, true)]
    [InlineData(GitHubRemoteWriteSignal.UnexpectedRemoteRefs, true)]
    public void Ambiguous_transport_signal_requires_reconciliation_and_never_allows_blind_retry(
        GitHubRemoteWriteSignal signal, bool requiresReconciliation)
    {
        var outcome = GitHubRemoteWriteOutcomeGuard.Classify(signal);
        Assert.Equal(requiresReconciliation, outcome.NeedsIndependentRemoteReconciliation);
        Assert.False(outcome.MayAutomaticallyRetry);
        Assert.False(outcome.ExecutionAuthorized);
    }

    private static GitHubCandidatePolicyReader Reader(HttpClient client) =>
        new(client, new FixedClock());

    private static string Repository() =>
        """{"id":2468,"full_name":"gh-org/sandbox","owner":{"login":"gh-org"},"archived":false,"disabled":false}""";

    private static HttpResponseMessage Json(int status, string body)
    {
        var reply = new HttpResponseMessage((HttpStatusCode)status) {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        return reply;
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> response) =>
        new(new Handler(response)) { Timeout = TimeSpan.FromSeconds(10) };

    private sealed class Handler(
        Func<HttpRequestMessage, HttpResponseMessage> factory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(factory(request));
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static GitHubCreateOnlyCandidatePacket CandidatePacket(
        IReadOnlyList<GitMirrorRef> candidates)
    {
        static string Sha(char c) => new(c, 64);
        var packet = new GitHubCreateOnlyPacket(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            2468, "gh-org", "sandbox", Sha('a'), Sha('b'), Sha('c'),
            Sha('d'), Sha('e'), Sha('f'), Sha('1'), Sha('2'), Sha('3'),
            GitImportRehearsalIntegrity.SealRefs(
                "f044-create-only-candidate-refs-v1", candidates),
            Sha('4'), candidates.Count, 0, 0,
            GitHubCreateOnlyPacketIntegrity.State, false, false, false, false);
        return new(packet, candidates);
    }
}
