using System.Net;
using System.Text;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.GitHub;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubRefObservationReaderTests
{
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTimeOffset Now =
        new(2026, 11, 5, 12, 30, 13, TimeSpan.Zero);

    [Fact]
    public async Task Only_two_fixed_host_GETs_return_stable_branch_and_annotated_tag_evidence()
    {
        var requests = new List<string>();
        using var client = new HttpClient(new FakeHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-pat", request.Headers.Authorization?.Parameter);
            Assert.Equal("2022-11-28",
                request.Headers.GetValues("X-GitHub-Api-Version").Single());
            requests.Add(request.RequestUri.AbsolutePath);
            return Response(HttpStatusCode.OK,
                request.RequestUri.AbsolutePath.EndsWith("/heads")
                    ? $"[{{\"ref\":\"refs/heads/main\",\"object\":{{\"sha\":\"{A}\",\"type\":\"commit\"}}}}]"
                    : $"[{{\"ref\":\"refs/tags/v1\",\"object\":{{\"sha\":\"{B}\",\"type\":\"tag\"}}}}]");
        }));
        var reader = new GitHubRefObservationReader(client, new StaticClock());
        var result = await reader.ReadAsync("team", "repo", "test-pat", default);

        Assert.Equal(new[] {
            "/repos/team/repo/git/matching-refs/heads",
            "/repos/team/repo/git/matching-refs/tags"
        }, requests);
        Assert.Equal(2, result.Refs.Count);
        Assert.Equal("tag", result.Refs[1].ObjectType);
        Assert.Equal(B, result.Refs[1].ObjectId);
        Assert.Equal(Now, result.ObservedAtUtc);
        Assert.Equal(64, result.EvidenceSha256.Length);
        Assert.Equal(2, result.AsMirrorRefs().Count);
        Assert.Equal(result.EvidenceSha256,
            GitHubRefObservationIntegrity.Seal("team", "repo", Now,
                result.Refs.Reverse().ToArray()).EvidenceSha256);
    }

    [Fact]
    public void Fingerprint_changes_when_type_object_id_or_collection_time_changes()
    {
        var refs = new[] { new GitHubObservedRef("refs/tags/v1", A, "tag") };
        var baseline = GitHubRefObservationIntegrity.Seal("team", "repo", Now, refs);
        Assert.NotEqual(baseline.EvidenceSha256, GitHubRefObservationIntegrity.Seal(
            "team", "repo", Now, [refs[0] with { ObjectType = "commit" }]).EvidenceSha256);
        Assert.NotEqual(baseline.EvidenceSha256, GitHubRefObservationIntegrity.Seal(
            "team", "repo", Now, [refs[0] with { ObjectId = B }]).EvidenceSha256);
        Assert.NotEqual(baseline.EvidenceSha256, GitHubRefObservationIntegrity.Seal(
            "team", "repo", Now.AddMilliseconds(1), refs).EvidenceSha256);
        Assert.NotEqual(baseline.EvidenceSha256, GitHubRefObservationIntegrity.Seal(
            "team", "another-repo", Now, refs).EvidenceSha256);
    }

    [Theory]
    [InlineData(404, "TargetRepositoryNotVisible")]
    [InlineData(401, "TargetRefAccessDenied")]
    [InlineData(403, "TargetRefAccessDenied")]
    [InlineData(429, "TargetRefRateLimited")]
    [InlineData(409, "TargetRefProviderUnavailable")]
    [InlineData(302, "TargetRefProviderUnavailable")]
    [InlineData(500, "TargetRefProviderUnavailable")]
    public async Task Provider_failures_do_not_turn_into_empty_target_snapshots(
        int httpStatus, string error)
    {
        var requests = 0;
        using var client = new HttpClient(new FakeHandler(_ =>
        {
            requests++;
            return Response((HttpStatusCode)httpStatus, "[]");
        }));
        var ex = await Assert.ThrowsAsync<GitHubRefObservationException>(() =>
            new GitHubRefObservationReader(client, new StaticClock())
                .ReadAsync("org", "repo", "token", default));
        Assert.Equal(error, ex.Code);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task Pagination_or_oversized_body_fails_closed_even_without_content_length()
    {
        using var linked = new HttpClient(new FakeHandler(_ =>
        {
            var response = Response(HttpStatusCode.OK, "[]");
            response.Headers.Add("Link",
                "<https://api.github.com/repos/org/repo/git/matching-refs/heads?page=2>; rel=\"next\"");
            return response;
        }));
        var ex = await Assert.ThrowsAsync<GitHubRefObservationException>(() =>
            new GitHubRefObservationReader(linked, new StaticClock())
                .ReadAsync("org", "repo", "pat", default));
        Assert.Equal("TargetRefPaginationUnverified", ex.Code);

        using var huge = new HttpClient(new FakeHandler(_ =>
            Response(HttpStatusCode.OK, new string('x', 2_000_001))));
        ex = await Assert.ThrowsAsync<GitHubRefObservationException>(() =>
            new GitHubRefObservationReader(huge, new StaticClock())
                .ReadAsync("org", "repo", "pat", default));
        Assert.Equal("TargetRefResponseLimit", ex.Code);
    }

    [Theory]
    [InlineData("refs/pull/1/head", "commit")]
    [InlineData("refs/heads/main", "tag")]
    [InlineData("refs/heads/main", "blob")]
    [InlineData("refs/heads/with space", "commit")]
    public async Task Unexpected_provider_namespace_type_or_name_rejects_observation(
        string name, string type)
    {
        using var client = new HttpClient(new FakeHandler(_ => Response(
            HttpStatusCode.OK,
            $"[{{\"ref\":\"{name}\",\"object\":{{\"sha\":\"{A}\",\"type\":\"{type}\"}}}}]")));
        var ex = await Assert.ThrowsAsync<GitHubRefObservationException>(() =>
            new GitHubRefObservationReader(client, new StaticClock())
                .ReadAsync("org", "repo", "pat", default));
        Assert.Contains(ex.Code, new[] {
            "InvalidTargetRefResponse", "InvalidTargetRefObservation"
        });
    }

    [Fact]
    public void Duplicate_and_case_aliases_are_never_silently_deduplicated()
    {
        var ex = Assert.Throws<GitHubRefObservationException>(() =>
            GitHubRefObservationIntegrity.Seal("org", "repo", Now, [
                new("refs/heads/main", A, "commit"),
                new("refs/heads/MAIN", B, "commit")
            ]));
        Assert.Equal("InvalidTargetRefObservation", ex.Code);
    }

    [Fact]
    public async Task Invalid_path_inputs_are_rejected_before_any_provider_request()
    {
        var calls = 0;
        using var client = new HttpClient(new FakeHandler(_ =>
        {
            calls++;
            return Response(HttpStatusCode.OK, "[]");
        }));
        var reader = new GitHubRefObservationReader(client, new StaticClock());
        foreach (var (org, repo) in new[] {
            ("org/other", "repo"), ("org", "../secret"),
            ("@host", "repo"), ("org", "bad repo")
        })
        {
            var ex = await Assert.ThrowsAsync<GitHubRefObservationException>(() =>
                reader.ReadAsync(org, repo, "pat", default));
            Assert.Equal("InvalidTargetRefRequest", ex.Code);
        }
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Empty_visible_repository_remains_non_authorizing_ref_evidence()
    {
        var count = 0;
        using var client = new HttpClient(new FakeHandler(_ =>
        {
            count++;
            return Response(HttpStatusCode.OK, "[]");
        }));
        var proof = await new GitHubRefObservationReader(client, new StaticClock())
            .ReadAsync("org", "repo", "pat", default);
        Assert.Equal(2, count);
        Assert.Empty(proof.Refs);
        Assert.Equal(64, proof.EvidenceSha256.Length);
        // No ReadyForExecution value exists on the GET proof.
        var compare = GitMirrorRefReconciler.Compare(new(
            Guid.NewGuid(), new string('a', 64), new string('b', 64),
            proof.EvidenceSha256, [], proof.AsMirrorRefs()));
        Assert.False(compare.ReadyForExecution);
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StaticClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> callback)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(callback(request));
    }
}
