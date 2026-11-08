using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Real PostgreSQL F-081→F-086→F-082→F-087/F-054→F-085 recovery.
// F-087 is GET evidence only: no GitHub token, network, push or retry.
public sealed class PostgresGitHubRecoveredReadbackTests(
    PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        using var services = new ServiceCollection()
            .AddDbContext<AppDbContext>(o =>
                o.UseNpgsql(fixture.ConnectionString))
            .BuildServiceProvider();
        await GitHubTargetPreviewLeaseSchema.InitializeAsync(services);
        await GitHubTransferAttemptSchema.InitializeAsync(services);
        await GitHubRemoteSandboxFenceSchema.InitializeAsync(services);
        await GitHubRemoteTestGrantSchema.InitializeAsync(services);
        await GitHubRemoteReadbackSchema.InitializeAsync(services);
        await GitHubRemoteTransmitHazardSchema.InitializeAsync(services);
        await GitHubRemoteAppPreflightEvidenceSchema.InitializeAsync(services);
        await GitHubRemoteSandboxReviewSessionSchema.InitializeAsync(services);
        await GitHubRemoteSandboxDispatchIntentSchema.InitializeAsync(services);
        await GitHubRemoteSandboxDispatchConfirmationSchema.InitializeAsync(services);
        await GitHubRemoteSandboxPreSendBarrierSchema.InitializeAsync(services);
        await GitHubRemoteTypedRecoveryBaselineSchema.InitializeAsync(services);
        await GitHubRemoteObjectReconciliationSchema.InitializeAsync(services);
        await GitHubRemotePostSendRecoverySchema.InitializeAsync(services);
    }
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Scenario(
        PostgresGitHubRemoteDispatchReviewTests.TestCase Setup,
        GitHubRemoteSandboxDispatchConfirmationReceipt Confirmation,
        GitHubRemoteTypedRecoveryBaselineReceipt Typed,
        GitHubRemoteSandboxPreSendBarrierReceipt Barrier);

    private static PostgresGitHubRemotePostSendRecoveryJournal Recovery(
        AppDbContext db, Scenario x) =>
        new(db, PostgresGitHubRemoteSandboxPreSendBarrierTests.Options(
            x.Setup),
            PostgresGitHubRemoteSandboxPreSendBarrierTests.Journal(
                db, x.Setup));

    private static PostgresGitHubRemoteRecoveredReadbackJournal Journal(
        AppDbContext db, Scenario x) =>
        new(db, TimeProvider.System,
            new(true, x.Typed.RepositoryId, "gh-org/sandbox"),
            PostgresGitHubRemoteTypedRecoveryBaselineTests.Journal(
                db, x.Setup),
            Recovery(db, x));

    private static async Task<Scenario> SeedAsync(AppDbContext db)
    {
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        var confirmation = await PostgresGitHubRemoteSandboxPreSendBarrierTests
            .SeedAsync(db, e);
        var actor = Guid.NewGuid();
        var snapshot = await PostgresGitHubRemoteTypedRecoveryBaselineTests
            .Capture(db, e, confirmation, actor);
        var marker = await PostgresGitHubRemoteSandboxPreSendBarrierTests
            .Journal(db, e).RecordPossibleSendAsync(
                confirmation.Id, confirmation.IntentId,
                e.Source.Packet.PreparationId,
                e.Source.Packet.PlanId, e.Source.Packet.ScopeId,
                actor, default);
        return new(e, confirmation, snapshot, marker);
    }

    private static GitHubRemoteReadbackEvidence Evidence(
        Scenario scenario, Guid observerId, bool drift = false)
    {
        var source = scenario.Typed;
        var now = DateTimeOffset.UtcNow;
        var typed = source.Baseline.Refs.Concat(
            source.CreateCandidates.Select(x =>
                new GitHubObservedRef(x.Name, x.ObjectId, "commit")))
            .Concat(drift ? [new GitHubObservedRef(
                "refs/heads/unexpected", new string('e', 40), "commit")] :
                [])
            .OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
        var remote = GitHubRefObservationIntegrity.Seal(
            source.Organization, source.Repository, now, typed);
        var tip = GitHubRemoteTipIntegrity.Seal(
            remote, source.CreateCandidates, now,
            source.CreateCandidates.Select(x =>
                new GitHubRemoteTipObject(
                    x.Name, x.ObjectId, "commit",
                    x.ObjectId, new string('f', 40), 0)).ToArray());
        return new(source.GrantId, source.RepositoryId, observerId,
            source.Baseline.Refs, source.CreateCandidates,
            new(remote, tip));
    }

    [Fact]
    public async Task F087_F054_can_record_after_original_grant_expiry_and_restart()
    {
        Scenario s;
        await using (var setup = fixture.NewContext())
        {
            s = await SeedAsync(setup);
            await setup.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE github_remote_test_grants
                SET deadline_utc={DateTimeOffset.UtcNow.AddMinutes(-1)}
                WHERE id={s.Barrier.GrantId}
                """);
            await setup.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE github_remote_sandbox_fences
                SET deadline_utc={DateTimeOffset.UtcNow.AddMinutes(-1)}
                WHERE repository_id={s.Barrier.RepositoryId}
                """);
        }
        await using var restarted = fixture.NewContext();
        var noProof = await Recovery(restarted, s).InspectScopedAsync(
            s.Barrier.Id, s.Confirmation.Id, s.Confirmation.IntentId,
            s.Typed.PreparationId, s.Typed.PlanId, s.Typed.ScopeId, default);
        Assert.NotNull(noProof);
        Assert.Equal(
            GitHubRemotePostSendRecoveryStates.AwaitingIndependentReadback,
            noProof.State);

        var observer = Guid.NewGuid();
        var e = Evidence(s, observer);
        var previous = await Journal(restarted, s)
            .RecordRecoveredAsync(s.Typed, e, default);
        Assert.Equal(GitHubRemoteReadbackStates.ExactTipEvidenceOnly,
            previous.State);
        Assert.False(previous.TransportAttempted);
        Assert.False(previous.RemoteWritesAuthorized);
        Assert.False(previous.MayAutomaticallyRetry);
        var historical = await Recovery(restarted, s).InspectScopedAsync(
            s.Barrier.Id, s.Confirmation.Id, s.Confirmation.IntentId,
            s.Typed.PreparationId, s.Typed.PlanId, s.Typed.ScopeId, default);
        Assert.NotNull(historical);
        Assert.Equal(
            GitHubRemotePostSendRecoveryStates.TipsObservedNeedsClosure,
            historical.State);
        Assert.Equal(previous.Id, historical.ReadbackId);
        Assert.False(historical.CanRetry);
        Assert.False(historical.CanDispatch);
        Assert.False(historical.MigrationComplete);

        await using var second = fixture.NewContext();
        var replay = await Journal(second, s)
            .RecordRecoveredAsync(s.Typed, e, default);
        Assert.Equal(previous.Id, replay.Id);
        Assert.Single(await second.GitHubRemoteReadbacks.AsNoTracking()
            .Where(x => x.GrantId == s.Typed.GrantId).ToArrayAsync());
        Assert.Single(await second.GitHubRemoteReadbackEvents.AsNoTracking()
            .Where(x => x.ReadbackId == previous.Id).ToArrayAsync());
    }

    [Fact]
    public async Task F087_unrelated_ref_mismatch_persists_review_but_never_closure()
    {
        await using var db = fixture.NewContext();
        var s = await SeedAsync(db);
        var e = Evidence(s, Guid.NewGuid(), drift: true);
        var result = await Journal(db, s).RecordRecoveredAsync(s.Typed, e, default);
        Assert.Equal(GitHubRemoteReadbackStates.OperatorReviewRequired,
            result.State);
        var recovery = await Recovery(db, s).InspectScopedAsync(
            s.Barrier.Id, s.Confirmation.Id, s.Confirmation.IntentId,
            s.Typed.PreparationId, s.Typed.PlanId, s.Typed.ScopeId, default);
        Assert.NotNull(recovery);
        Assert.Equal(GitHubRemotePostSendRecoveryStates.OperatorReviewRequired,
            recovery.State);
        Assert.Null(recovery.ReconciliationId);
        Assert.False(recovery.CanRetry);
    }

    [Fact]
    public async Task F087_cannot_accept_sender_actor_or_a_second_different_observer()
    {
        await using var db = fixture.NewContext();
        var s = await SeedAsync(db);
        var bad = Evidence(s, s.Typed.CapturedBy);
        var err = await Assert.ThrowsAsync<GitHubRemoteReadbackException>(
            () => Journal(db, s).RecordRecoveredAsync(s.Typed, bad, default));
        Assert.Equal("RecoveredReadbackSnapshotMismatch", err.Code);
        var a = await Journal(db, s).RecordRecoveredAsync(
            s.Typed, Evidence(s, Guid.NewGuid()), default);
        err = await Assert.ThrowsAsync<GitHubRemoteReadbackException>(
            () => Journal(db, s).RecordRecoveredAsync(
                s.Typed, Evidence(s, Guid.NewGuid()), default));
        Assert.Equal("RecoveredReadbackConflict", err.Code);
        Assert.Single(await db.GitHubRemoteReadbackEvents.AsNoTracking()
            .Where(x => x.ReadbackId == a.Id).ToArrayAsync());
    }

    [Fact]
    public async Task F087_no_F082_barrier_cannot_record_independent_F054()
    {
        await using var db = fixture.NewContext();
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        var confirmation = await PostgresGitHubRemoteSandboxPreSendBarrierTests
            .SeedAsync(db, e);
        var typed = await PostgresGitHubRemoteTypedRecoveryBaselineTests
            .Capture(db, e, confirmation, Guid.NewGuid());
        var dummy = new Scenario(e, confirmation, typed,
            new GitHubRemoteSandboxPreSendBarrierReceipt(
                Guid.NewGuid(), confirmation.Id, confirmation.IntentId,
                confirmation.SessionId, typed.HazardId, typed.GrantId,
                typed.RepositoryId, typed.CapturedBy,
                new string('a', 64), new string('b', 64),
                new string('c', 64), typed.PacketSha256,
                typed.CandidateSetSha256, new string('d', 64),
                DateTimeOffset.UtcNow, "", "", false, false, false, false));
        var error = await Assert.ThrowsAsync<GitHubRemoteReadbackException>(
            () => Journal(db, dummy).RecordRecoveredAsync(
                typed, Evidence(dummy, Guid.NewGuid()), default));
        Assert.Equal("RecoveredReadbackHistoricalProofMissing", error.Code);
        Assert.Empty(await db.GitHubRemoteReadbacks.AsNoTracking()
            .Where(x => x.GrantId == typed.GrantId).ToArrayAsync());
    }
}
