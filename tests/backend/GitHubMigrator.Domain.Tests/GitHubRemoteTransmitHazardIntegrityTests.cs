using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubRemoteTransmitHazardIntegrityTests
{
    private static string Sha(char c) => new(c, 64);

    [Fact]
    public void Independent_fifth_actor_with_matching_F047_F050_F052_can_prepare_only()
    {
        var e = Fixture();
        GitHubRemoteTransmitHazardIntegrity.RequireDependencies(
            e.Grant, e.Fence, e.Attempt, e.Options, e.Actor, e.Now);
        var token = Guid.NewGuid();
        var digest = GitHubRemoteTransmitHazardIntegrity.AbortTokenDigest(token);
        Assert.Equal(64, digest.Length);
        Assert.NotEqual(
            GitHubRemoteTestGrantIntegrity.TokenDigest(token), digest);
        Assert.NotEqual(
            GitHubRemoteSandboxFenceIntegrity.TokenDigest(token), digest);
    }

    [Fact]
    public void Every_previous_actor_and_F047_F050_F052_flag_drift_is_refused()
    {
        var e = Fixture();
        foreach (var actor in new[] {
            e.Attempt.RequestedBy, e.Attempt.ReviewedBy!.Value,
            e.Fence.RequestedBy, e.Fence.ReviewedBy!.Value,
            e.Grant.RequestedBy, e.Grant.ReviewedBy!.Value
        })
            Refuse(e, actorId: actor);

        Refuse(e with { Grant = e.Grant with {
            RemoteWritesPerformed = true } });
        Refuse(e with { Grant = e.Grant with {
            State = GitHubRemoteTestGrantStates.RecoveryRequired } });
        Refuse(e with { Fence = e.Fence with {
            Generation = 3 } });
        Refuse(e with { Attempt = e.Attempt with {
            TransportAttempted = true } });
        Refuse(e with { Attempt = e.Attempt with {
            PolicySha256 = Sha('e') } });
        Refuse(e with { Fence = e.Fence with {
            DeadlineUtc = e.Now.AddSeconds(-1) } });
        Refuse(e with { Options = new(false, e.Options.AllowedRepositoryId,
            e.Options.AllowedRepository) }, "RemoteTransmitHazardDisabled");
    }

    private static void Refuse(
        FixtureData f, Guid? actorId = null,
        string expected = "RemoteTransmitHazardBarrierChanged")
    {
        var ex = Assert.Throws<GitHubRemoteTransmitHazardException>(() =>
            GitHubRemoteTransmitHazardIntegrity.RequireDependencies(
                f.Grant, f.Fence, f.Attempt, f.Options,
                actorId ?? f.Actor, f.Now));
        Assert.Equal(expected, ex.Code);
    }

    private sealed record FixtureData(
        GitHubRemoteTestGrantStatus Grant,
        GitHubRemoteSandboxFenceStatus Fence,
        GitHubTransferAttemptView Attempt,
        GitHubRemoteTransmitHazardOptions Options,
        Guid Actor, DateTimeOffset Now);

    private static FixtureData Fixture()
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var prep = Guid.NewGuid();
        var plan = Guid.NewGuid();
        var scope = Guid.NewGuid();
        var request = Guid.NewGuid();
        var repo = 987654321L;
        var a = new GitHubTransferAttemptView(
            id, prep, plan, scope, repo, "gh-org", "test-sandbox",
            Sha('a'), Sha('b'), Sha('c'), 1, 1,
            GitHubTransferAttemptStates.BlockedAtWriteBarrier,
            Guid.NewGuid(), Guid.NewGuid(),
            now, now, now, null, false, false, false, false);
        var f = new GitHubRemoteSandboxFenceStatus(
            request, repo, "gh-org", "test-sandbox",
            prep, plan, scope, id, Sha('a'), Sha('c'), Sha('b'), 1,
            GitHubRemoteSandboxFenceStates.ReviewedWriteDisabled,
            Guid.NewGuid(), Guid.NewGuid(), now.AddSeconds(20), now,
            false, false, false);
        var g = new GitHubRemoteTestGrantStatus(
            Guid.NewGuid(), repo, "gh-org", "test-sandbox", prep, plan,
            scope, id, request, 1, Sha('a'), Sha('c'), Sha('b'),
            Sha('d'), Sha('e'),
            GitHubRemoteTestGrantStates.ReviewedWriteDisabled,
            Guid.NewGuid(), Guid.NewGuid(), now.AddSeconds(20), now,
            false, false, false);
        return new(g, f, a, new(true, repo, "gh-org/test-sandbox"),
            Guid.NewGuid(), now);
    }
}
