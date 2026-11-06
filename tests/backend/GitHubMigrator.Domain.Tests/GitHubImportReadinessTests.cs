using System.Net;
using System.Text;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.GitHub;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubImportReadinessTests
{
    [Fact]
    public async Task Valid_200_reports_push_as_advisory_only_and_never_unlocks_execution()
    {
        using var client = new HttpClient(new Stub(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Equal("/repos/gh-org/occupied", request.RequestUri.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("sensitive-pat", request.Headers.Authorization?.Parameter);
            return Json(HttpStatusCode.OK, """
                {"full_name":"gh-org/occupied","owner":{"login":"gh-org"},
                 "archived":false,"disabled":false,"permissions":{"push":true}}
                """);
        }));
        var now = TimeProvider.System;
        var read = await new GitHubImportRepositoryReader(client, now)
            .ObserveAsync("gh-org", "occupied", "sensitive-pat", default);
        Assert.Equal("Visible", read.Status);
        Assert.Equal("ReportedAllowed", read.ReportedPush);
        var input = Input(read);
        var digest = GitHubImportReadinessIntegrity.Seal(input);
        Assert.Equal(64, digest.Length);
        Assert.Equal(digest, GitHubImportReadinessIntegrity.Seal(input));
        var blockers = GitHubImportReadinessIntegrity.Blockers(input);
        Assert.Contains("ContentsWriteScopeNotVerified", blockers);
        Assert.Contains("BranchRulesNotVerified", blockers);
        Assert.Contains("GitHubImportExecutorNotImplemented", blockers);
        Assert.Contains("WriteAuthorizationNotGranted", blockers);
        Assert.DoesNotContain("RepositoryPushPermissionNotReported", blockers);
    }

    [Theory]
    [InlineData(401, "Unauthorized")]
    [InlineData(403, "Forbidden")]
    [InlineData(404, "NotVisibleOrMissing")]
    [InlineData(429, "RateLimited")]
    [InlineData(500, "Unavailable")]
    [InlineData(302, "Unavailable")]
    public async Task Negative_or_ambiguous_HTTP_results_never_prove_writes(
        int status, string expected)
    {
        using var client = new HttpClient(new Stub(_ =>
            Json((HttpStatusCode)status, "{}")));
        var proof = await new GitHubImportRepositoryReader(client, TimeProvider.System)
            .ObserveAsync("gh-org", "occupied", "pat", default);
        Assert.Equal(expected, proof.Status);
        Assert.Equal("Unverified", proof.ReportedPush);
        Assert.Contains("TargetRepositoryNotVerifiedVisible",
            GitHubImportReadinessIntegrity.Blockers(Input(proof)));
    }

    [Theory]
    [InlineData("""{"full_name":"wrong-org/occupied","owner":{"login":"gh-org"},"archived":false,"disabled":false,"permissions":{"push":true}}""")]
    [InlineData("""{"full_name":"gh-org/occupied","owner":{"login":"evil-org"},"archived":false,"disabled":false,"permissions":{"push":true}}""")]
    [InlineData("""{"full_name":"gh-org/occupied","owner":{"login":"gh-org"},"archived":false,"permissions":{"push":true}}""")]
    [InlineData("""{"full_name":"gh-org/occupied","owner":{"login":"gh-org"},"archived":"false","disabled":false,"permissions":{"push":true}}""")]
    [InlineData("""not-json""")]
    public async Task Wrong_identity_or_malformed_provider_JSON_is_not_trusted(
        string json)
    {
        using var client = new HttpClient(new Stub(_ =>
            Json(HttpStatusCode.OK, json)));
        var proof = await new GitHubImportRepositoryReader(client, TimeProvider.System)
            .ObserveAsync("gh-org", "occupied", "pat", default);
        Assert.Equal("InvalidResponse", proof.Status);
        Assert.Equal("Unverified", proof.ReportedPush);
    }

    [Fact]
    public async Task Missing_push_permission_is_not_construed_as_write_access()
    {
        using var client = new HttpClient(new Stub(_ => Json(HttpStatusCode.OK,
            """{"full_name":"gh-org/occupied","owner":{"login":"gh-org"},"archived":true,"disabled":true}""")));
        var proof = await new GitHubImportRepositoryReader(client, TimeProvider.System)
            .ObserveAsync("gh-org", "occupied", "pat", default);
        var blockers = GitHubImportReadinessIntegrity.Blockers(Input(proof));
        Assert.Equal("Unverified", proof.ReportedPush);
        Assert.Contains("TargetRepositoryArchived", blockers);
        Assert.Contains("TargetRepositoryDisabled", blockers);
        Assert.Contains("RepositoryPushPermissionNotReported", blockers);
    }

    [Fact]
    public async Task Invalid_repositories_and_oversized_json_fail_closed()
    {
        var calls = 0;
        using var client = new HttpClient(new Stub(_ =>
        {
            calls++;
            return Json(HttpStatusCode.OK, new string('a', 70_000));
        }));
        var reader = new GitHubImportRepositoryReader(client, TimeProvider.System);
        foreach (var invalid in new[] { "..", "../secret", "repo?admin=true", "", "a/b" })
            Assert.Equal("ImportReadinessProbeInvalid",
                (await Assert.ThrowsAsync<GitHubImportReadinessException>(() =>
                    reader.ObserveAsync("gh-org", invalid, "pat", default))).Code);
        Assert.Equal(0, calls);
        var response = await reader.ObserveAsync("gh-org", "valid-repo", "pat", default);
        Assert.Equal("InvalidResponse", response.Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void SHA_binding_includes_source_and_target_proofs_and_risk_flags()
    {
        var input = Input(new("Visible", "ReportedAllowed", false, false,
            DateTimeOffset.UtcNow));
        var first = GitHubImportReadinessIntegrity.Seal(input);
        Assert.NotEqual(first, GitHubImportReadinessIntegrity.Seal(
            input with { SourceBundleSha256 = new string('a', 64) }));
        Assert.NotEqual(first, GitHubImportReadinessIntegrity.Seal(
            input with { TargetEvidenceSha256 = new string('b', 64) }));
        Assert.NotEqual(first, GitHubImportReadinessIntegrity.Seal(
            input with { DivergedRefs = 1 }));
        Assert.NotEqual(first, GitHubImportReadinessIntegrity.Seal(
            input with { Observation = input.Observation with { ReportedPush = "ReportedDenied" } }));
        Assert.Contains("DivergedRefsBlocked", GitHubImportReadinessIntegrity.Blockers(
            input with { DivergedRefs = 2 }));
        Assert.Contains("TargetOnlyRefsPreserved", GitHubImportReadinessIntegrity.Blockers(
            input with { TargetOnlyRefs = 2 }));
    }

    private static GitHubImportReadinessInput Input(GitHubImportRepositoryObservation proof) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "repo-1", "occupied", "gh-org", Guid.NewGuid(), DateTimeOffset.UtcNow,
            new string('c', 64), new string('d', 64), new string('e', 64),
            new string('f', 64), 0, 0, proof, Guid.NewGuid());

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(responder(request));
    }
}
