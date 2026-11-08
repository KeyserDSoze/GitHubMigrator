using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Real PostgreSQL17: F-081→F-086→F-082→F-054→F-058→F-085
// timeline is derived from existing immutable evidence. No sender.
public sealed class PostgresGitHubRemoteRecoveryWorkbenchTests(
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

    private static string Sha(char c) => new(c, 64);

    private static PostgresGitHubRemoteRecoveryWorkbenchReader Reader(
        AppDbContext db, PostgresGitHubRecoveredReadbackTests.Scenario s,
        bool enabled = true, bool typedEnabled = true)
    {
        var scope = PostgresGitHubRemoteSandboxPreSendBarrierTests
            .Options(s.Setup);
        return new(db, new(enabled, s.Typed.RepositoryId, "gh-org/sandbox"),
            new(typedEnabled, s.Typed.RepositoryId, "gh-org/sandbox"),
            scope,
            PostgresGitHubRecoveredReadbackTests.Recovery(db, s),
            PostgresGitHubRemoteTypedRecoveryBaselineTests.Journal(
                db, s.Setup));
    }

    private static Task<GitHubRemoteRecoveryWorkbenchPage> List(
        AppDbContext db, PostgresGitHubRecoveredReadbackTests.Scenario s,
        Guid? after = null, int take = 12, bool enabled = true,
        bool typedEnabled = true) =>
        Reader(db, s, enabled, typedEnabled).ListAsync(
            s.Typed.PreparationId, s.Typed.PlanId, after, take, default);

    [Fact]
    public async Task F088_restart_shows_pending_F082_even_if_no_remote_evidence()
    {
        PostgresGitHubRecoveredReadbackTests.Scenario s;
        await using (var setup = fixture.NewContext())
            s = await PostgresGitHubRecoveredReadbackTests.SeedAsync(setup);

        await using var restarted = fixture.NewContext();
        var page = await List(restarted, s);
        var row = Assert.Single(page.Items);
        Assert.Equal(s.Barrier.Id, row.BarrierId);
        Assert.Equal("gh-org", row.Organization);
        Assert.Equal("sandbox", row.Repository);
        Assert.Equal(s.Typed.RepositoryId, row.RepositoryId);
        Assert.Equal(s.Typed.CreateCandidates.Count, row.CreateCandidateCount);
        Assert.Equal(s.Typed.Baseline.Refs.Count, row.BaselineRefCount);
        Assert.Equal(s.Typed.EvidenceSha256, row.TypedBaselineEvidenceSha256);
        Assert.Equal(s.Barrier.EvidenceSha256, row.BarrierEvidenceSha256);
        Assert.Equal(
            GitHubRemotePostSendRecoveryStates.AwaitingIndependentReadback,
            row.State);
        Assert.Equal(GitHubRemoteRecoveryWorkbenchActions.ObserveTips,
            row.RecommendedNextAction);
        Assert.True(row.NeedsOperatorAttention);
        Assert.False(row.CanPush);
        Assert.False(row.CanRetry);
        Assert.False(row.CanDispatch);
        Assert.False(row.MigrationComplete);
        Assert.Null(page.NextAfter);
        Assert.False(page.CanPush);
        Assert.DoesNotContain("refs/heads/", JsonSerializer.Serialize(page));
        Assert.DoesNotContain("ghs_", JsonSerializer.Serialize(page));

        // The exact cursor must not silently skip/duplicate an epoch;
        // a cursor from another plan must be denied, not accepted.
        var beyond = await List(restarted, s, row.BarrierId);
        Assert.Empty(beyond.Items);
        var badCursor = await Assert.ThrowsAsync<
            GitHubRemoteRecoveryWorkbenchException>(() =>
                List(restarted, s, Guid.NewGuid()));
        Assert.Equal("RecoveryWorkbenchCursorInvalid", badCursor.Code);
        foreach (var invalid in new[] { 0, 21 })
            Assert.Equal("RecoveryWorkbenchQueryInvalid",
                (await Assert.ThrowsAsync<
                    GitHubRemoteRecoveryWorkbenchException>(() =>
                    List(restarted, s, take: invalid))).Code);
        var otherPlan = await Reader(restarted, s).ListAsync(
            s.Typed.PreparationId, Guid.NewGuid(), null, 12, default);
        Assert.Empty(otherPlan.Items);
    }

    [Fact]
    public async Task F088_tips_full_objects_and_checkpoint_update_operational_state()
    {
        await using var db = fixture.NewContext();
        var s = await PostgresGitHubRecoveredReadbackTests.SeedAsync(db);
        var evidence = PostgresGitHubRecoveredReadbackTests.Evidence(
            s, Guid.NewGuid());
        var tips = await PostgresGitHubRecoveredReadbackTests.Journal(
            db, s).RecordRecoveredAsync(s.Typed, evidence, default);
        var tip = Assert.Single((await List(db, s)).Items);
        Assert.Equal(GitHubRemotePostSendRecoveryStates.TipsObservedNeedsClosure,
            tip.State);
        Assert.Equal(GitHubRemoteRecoveryWorkbenchActions.VerifyFullObjects,
            tip.RecommendedNextAction);
        Assert.Equal(tips.JournalEvidenceSha256,
            tip.ReadbackEvidenceSha256);
        Assert.Null(tip.ReconciliationId);

        var now = DateTimeOffset.UtcNow;
        var domain = new GitHubRemoteObjectReconciliationStatus(
            Guid.NewGuid(), s.Typed.GrantId, tips.Id, s.Typed.HazardId,
            s.Typed.RepositoryId, Guid.NewGuid(),
            GitHubRemoteObjectReconciliationStates.FullCopyEvidenceOnly,
            tips.BaselineSha256, tips.CandidateSetSha256,
            tips.RefSetSha256, Sha('a'), Sha('b'), Sha('c'), "",
            Guid.NewGuid(), now, now, false, false, false, false);
        var receipt = new GitHubRemoteObjectReconciliationRecord
        {
            Id = domain.Id, GrantId = domain.GrantId,
            ReadbackId = domain.ReadbackId, HazardId = domain.HazardId,
            RepositoryId = domain.RepositoryId,
            SourceSnapshotId = domain.SourceSnapshotId,
            State = domain.State,
            OriginalBaselineSha256 = domain.OriginalBaselineSha256,
            CandidateSetSha256 = domain.CandidateSetSha256,
            TargetRefSetSha256 = domain.TargetRefSetSha256,
            RemoteRefObservationSha256 = domain.RemoteRefObservationSha256,
            FullObjectClosureSha256 = domain.FullObjectClosureSha256,
            RemoteMirrorReceiptSha256 = domain.RemoteMirrorReceiptSha256,
            EvidenceSha256 = GitHubRemotePostSendRecoveryIntegrity
                .FullClosureDigest(domain),
            ObservedBy = domain.ObservedBy, VerifiedAtUtc = now,
            RecordedAtUtc = now, RemotePushInvoked = false,
            RemoteWritesAuthorized = false, MayAutomaticallyRetry = false,
            MigrationComplete = false
        };
        db.GitHubRemoteObjectReconciliations.Add(receipt);
        db.GitHubRemoteObjectReconciliationEvents.Add(new()
        {
            ReconciliationId = receipt.Id,
            Type = "Recorded",
            ActorId = receipt.ObservedBy, OccurredAtUtc = now
        });
        await db.SaveChangesAsync();
        var closure = Assert.Single((await List(db, s)).Items);
        Assert.Equal(
            GitHubRemotePostSendRecoveryStates.IndependentClosureObserved,
            closure.State);
        Assert.Equal(GitHubRemoteRecoveryWorkbenchActions.CertifyClosure,
            closure.RecommendedNextAction);
        Assert.Equal(receipt.EvidenceSha256,
            closure.ReconciliationEvidenceSha256);
        Assert.False(closure.MigrationComplete);

        var checkpoint = await PostgresGitHubRecoveredReadbackTests
            .Recovery(db, s).RecordIndependentClosureAsync(
                s.Barrier.Id, s.Confirmation.Id, s.Confirmation.IntentId,
                s.Typed.PreparationId, s.Typed.PlanId, s.Typed.ScopeId,
                Guid.NewGuid(), default);
        await using var rebooted = fixture.NewContext();
        var verified = Assert.Single((await List(rebooted, s)).Items);
        Assert.Equal(
            GitHubRemotePostSendRecoveryStates.CheckpointedIndependentClosure,
            verified.State);
        Assert.Equal(GitHubRemoteRecoveryWorkbenchActions.None,
            verified.RecommendedNextAction);
        Assert.Equal(checkpoint.Id, verified.ClosureCheckpointId);
        Assert.Equal(checkpoint.EvidenceSha256,
            verified.ClosureCheckpointEvidenceSha256);
        Assert.False(verified.CanPush);
        Assert.False(verified.CanRetry);
        Assert.False(verified.MigrationComplete);
    }

    [Fact]
    public async Task F088_disabled_or_missing_F086_does_not_fabricate_remote_success()
    {
        await using var db = fixture.NewContext();
        var s = await PostgresGitHubRecoveredReadbackTests.SeedAsync(db);
        var disabled = await Assert.ThrowsAsync<
            GitHubRemoteRecoveryWorkbenchException>(() =>
                List(db, s, enabled: false));
        Assert.Equal("RecoveryWorkbenchDisabled", disabled.Code);
        var withoutTyped = Assert.Single(
            (await List(db, s, typedEnabled: false)).Items);
        Assert.False(withoutTyped.TypedBaselineAvailable);
        Assert.Equal(GitHubRemoteRecoveryWorkbenchActions.OperatorReview,
            withoutTyped.RecommendedNextAction);
        Assert.Null(withoutTyped.TypedBaselineEvidenceSha256);
        Assert.False(withoutTyped.CanPush);
        Assert.False(withoutTyped.CanRetry);
    }

    [Fact]
    public async Task F088_corrupted_F054_audit_is_not_hidden_behind_a_dashboard_row()
    {
        await using var db = fixture.NewContext();
        var s = await PostgresGitHubRecoveredReadbackTests.SeedAsync(db);
        var evidence = PostgresGitHubRecoveredReadbackTests.Evidence(
            s, Guid.NewGuid());
        var status = await PostgresGitHubRecoveredReadbackTests.Journal(
            db, s).RecordRecoveredAsync(s.Typed, evidence, default);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM github_remote_readback_events
            WHERE readback_id={status.Id}
            """);
        var refused = await Assert.ThrowsAsync<
            GitHubRemotePostSendRecoveryException>(() => List(db, s));
        Assert.Equal("PostSendReadbackHistoryCorrupt", refused.Code);
    }
}
