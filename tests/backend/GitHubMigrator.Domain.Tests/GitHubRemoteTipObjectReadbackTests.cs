using System.Net;
using System.Text;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.GitHub;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubRemoteTipObjectReadbackTests
{
    private static readonly string A = new('a', 40);
    private static readonly string B = new('b', 40);
    private static readonly string C = new('c', 40);
    private static readonly string D = new('d', 40);
    private static readonly string E = new('e', 40);
    private static readonly string F = new('f', 40);
    private static readonly DateTimeOffset Now =
        new(2026, 11, 6, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Full_ref_set_annotated_tag_target_and_commit_tips_are_checked_with_GET_only()
    {
        var calls = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("ephemeral-token", request.Headers.Authorization?.Parameter);
            calls.Add(request.RequestUri.AbsolutePath);
            return Reply(request.RequestUri.AbsolutePath);
        }));
        var created = Created();
        var observed = await new GitHubRemoteTipObjectReader(
            client, new Clock()).ReadAsync(
                "org", "sandbox", "ephemeral-token", created, default);
        Assert.Equal(7, calls.Count); // 2 full reads + 3 object GETs
        Assert.Equal(2, calls.Count(x => x.EndsWith("/matching-refs/heads")));
        Assert.Equal(2, calls.Count(x => x.EndsWith("/matching-refs/tags")));
        Assert.All(observed.TipReadback.Tips,
            tip => Assert.Equal(D, tip.CommitTreeId));
        Assert.Equal(1, observed.TipReadback.Tips[1].TagObjectsTraversed);
        Assert.Equal(C, observed.TipReadback.Tips[1].CommitObjectId);

        var proof = GitHubRemoteTipIntegrity.Inspect(
            [new GitMirrorRef("refs/heads/main", E)], created,
            observed.RemoteRefs, observed.TipReadback);
        Assert.Equal("ExactRemoteRefsAndTipObjectsObserved", proof.State);
        Assert.Equal(64, proof.EvidenceSha256.Length);
        Assert.Equal(64, observed.TipReadback.EvidenceSha256.Length);
        Assert.False(proof.MayAutomaticallyRetry);
        Assert.False(proof.RemoteWritesAuthorized);
        Assert.False(observed.TipReadback.MayAutomaticallyRetry);
        Assert.False(observed.TipReadback.RemoteWritesAuthorized);
    }

    [Theory]
    [InlineData("missing", "RemoteTipObjectNotVisible")]
    [InlineData("badsha", "RemoteTipCommitInvalid")]
    [InlineData("badtag", "RemoteTipTagChainInvalid")]
    [InlineData("cycle", "RemoteTipTagChainInvalid")]
    [InlineData("diverged", "RemoteTipRefChangedDuringReadback")]
    public async Task Missing_forged_or_moving_objects_fail_closed(
        string scenario, string code)
    {
        var headsReads = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/matching-refs/heads"))
                headsReads++;
            if (scenario == "missing" && path.EndsWith("/git/commits/" + A))
                return Response(HttpStatusCode.NotFound, "{}");
            if (scenario == "badsha" && path.EndsWith("/git/commits/" + A))
                return Response(HttpStatusCode.OK,
                    $"{{\"sha\":\"{C}\",\"tree\":{{\"sha\":\"{D}\"}}}}");
            if (scenario == "badtag" && path.EndsWith("/git/tags/" + B))
                return Response(HttpStatusCode.OK,
                    $"{{\"sha\":\"{B}\",\"object\":{{\"sha\":\"{C}\",\"type\":\"blob\"}}}}");
            if (scenario == "cycle" && path.EndsWith("/git/tags/" + B))
                return Response(HttpStatusCode.OK,
                    $"{{\"sha\":\"{B}\",\"object\":{{\"sha\":\"{B}\",\"type\":\"tag\"}}}}");
            if (scenario == "diverged" && headsReads == 2 &&
                path.EndsWith("/matching-refs/heads"))
                return Response(HttpStatusCode.OK, Heads(F));
            return Reply(path);
        }));
        var ex = await Assert.ThrowsAsync<GitHubRemoteTipException>(() =>
            new GitHubRemoteTipObjectReader(client, new Clock())
                .ReadAsync("org", "sandbox", "token", Created(), default));
        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public async Task Invalid_path_and_duplicate_create_refs_reject_without_network()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(_ =>
        {
            calls++;
            return Response(HttpStatusCode.OK, "[]");
        }));
        var reader = new GitHubRemoteTipObjectReader(client, new Clock());
        await Assert.ThrowsAsync<GitHubRemoteTipException>(() =>
            reader.ReadAsync("../org", "repo", "token", Created(), default));
        await Assert.ThrowsAsync<GitHubRemoteTipException>(() =>
            reader.ReadAsync("org", "repo", "token",
                [Created()[0], Created()[0]], default));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Altered_readback_digest_or_false_full_ref_match_never_verifies()
    {
        var refs = GitHubRefObservationIntegrity.Seal(
            "org", "sandbox", Now, [
                new("refs/heads/feature", A, "commit"),
                new("refs/heads/main", E, "commit"),
                new("refs/tags/v2", B, "tag")
            ]);
        var created = Created();
        var objects = GitHubRemoteTipIntegrity.Seal(refs, created, Now, [
            new("refs/heads/feature", A, "commit", A, D, 0),
            new("refs/tags/v2", B, "tag", C, D, 1)
        ]);
        var falseProof = objects with {
            EvidenceSha256 = new string('0', 64)
        };
        Assert.Equal("RemoteTipEvidenceInvalid",
            Assert.Throws<GitHubRemoteTipException>(() =>
                GitHubRemoteTipIntegrity.Inspect(
                    [new("refs/heads/main", E)],
                    created, refs, falseProof)).Code);

        // Object tips may be valid while the full remote ref set contains
        // an unexpected ref. This still demands operator reconciliation.
        var changedRefs = GitHubRefObservationIntegrity.Seal(
            "org", "sandbox", Now, [
                ..refs.Refs,
                new("refs/heads/unexpected", F, "commit")
            ]);
        var changedProof = GitHubRemoteTipIntegrity.Seal(
            changedRefs, created, Now, objects.Tips);
        var review = GitHubRemoteTipIntegrity.Inspect(
            [new("refs/heads/main", E)], created,
            changedRefs, changedProof);
        Assert.Equal("RemoteTipReadbackRequiresOperator", review.State);
        Assert.False(review.MayAutomaticallyRetry);
    }

    private static GitMirrorRef[] Created() => [
        new("refs/heads/feature", A), new("refs/tags/v2", B)
    ];

    private static string Heads(string main) =>
        $"[{{\"ref\":\"refs/heads/feature\",\"object\":{{\"sha\":\"{A}\",\"type\":\"commit\"}}}}," +
        $"{{\"ref\":\"refs/heads/main\",\"object\":{{\"sha\":\"{main}\",\"type\":\"commit\"}}}}]";

    private static HttpResponseMessage Reply(string path)
    {
        if (path.EndsWith("/matching-refs/heads"))
            return Response(HttpStatusCode.OK, Heads(E));
        if (path.EndsWith("/matching-refs/tags"))
            return Response(HttpStatusCode.OK,
                $"[{{\"ref\":\"refs/tags/v2\",\"object\":{{\"sha\":\"{B}\",\"type\":\"tag\"}}}}]");
        if (path.EndsWith("/git/commits/" + A))
            return Response(HttpStatusCode.OK,
                $"{{\"sha\":\"{A}\",\"tree\":{{\"sha\":\"{D}\"}}}}");
        if (path.EndsWith("/git/commits/" + C))
            return Response(HttpStatusCode.OK,
                $"{{\"sha\":\"{C}\",\"tree\":{{\"sha\":\"{D}\"}}}}");
        if (path.EndsWith("/git/tags/" + B))
            return Response(HttpStatusCode.OK,
                $"{{\"sha\":\"{B}\",\"object\":{{\"sha\":\"{C}\",\"type\":\"commit\"}}}}");
        throw new InvalidOperationException("Unexpected outbound route: " + path);
    }

    private static HttpResponseMessage Response(
        HttpStatusCode status, string json) => new(status) {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(reply(request));
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
