using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// F-085 uses the REAL F-081 -> F-082 PostgreSQL grant/hazard/actor
// history, with independent persisted F-054 and F-058 receipts. No
// external GitHub traffic or retry path is involved.
public sealed class PostgresGitHubRemotePostSendRecoveryTests(
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
        await GitHubRemoteObjectReconciliationSchema.InitializeAsync(services);
        await GitHubRemotePostSendRecoverySchema.InitializeAsync(services);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Sha(char x) => new(x, 64);

    private sealed record Scenario(
        PostgresGitHubRemoteDispatchReviewTests.TestCase TestCase,
        GitHubRemoteSandboxPreSendBarrierReceipt Barrier)
    {
        public GitHubCreateOnlyPacket Packet => TestCase.Source.Packet;
    }

    private static PostgresGitHubRemotePostSendRecoveryJournal Journal(
        AppDbContext db, Scenario s) =>
        new(db, PostgresGitHubRemoteSandboxPreSendBarrierTests.Options(
                s.TestCase),
            PostgresGitHubRemoteSandboxPreSendBarrierTests.Journal(
                db, s.TestCase));

    private static async Task<Scenario> SeedAsync(AppDbContext db)
    {
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        var confirmation =
            await PostgresGitHubRemoteSandboxPreSendBarrierTests.SeedAsync(
                db, e);
        var p = e.Source.Packet;
        var marker = await PostgresGitHubRemoteSandboxPreSendBarrierTests
            .Journal(db, e).RecordPossibleSendAsync(
                confirmation.Id, confirmation.IntentId,
                p.PreparationId, p.PlanId, p.ScopeId,
                Guid.NewGuid(), default);
        return new(e, marker);
    }

    private static Task<GitHubRemotePostSendRecoveryInspection?> InspectAsync(
        AppDbContext db, Scenario s) =>
        Journal(db, s).InspectScopedAsync(
            s.Barrier.Id, s.Barrier.ConfirmationId, s.Barrier.IntentId,
            s.Packet.PreparationId, s.Packet.PlanId, s.Packet.ScopeId, default);

    private static Task<GitHubRemotePostSendClosureCheckpoint> CheckpointAsync(
        AppDbContext db, Scenario s, Guid actor) =>
        Journal(db, s).RecordIndependentClosureAsync(
            s.Barrier.Id, s.Barrier.ConfirmationId, s.Barrier.IntentId,
            s.Packet.PreparationId, s.Packet.PlanId, s.Packet.ScopeId,
            actor, default);

    private static async Task<GitHubRemoteReadbackRecord> InsertTipsAsync(
        AppDbContext db, Scenario s, bool exact = true,
        bool includeAudit = true)
    {
        var hazard = await db.GitHubRemoteTransmitHazards.AsNoTracking()
            .SingleAsync(x => x.Id == s.Barrier.HazardId);
        var after = DateTimeOffset.UtcNow;
        var receipt = new GitHubRemoteReadbackRecord
        {
            Id = Guid.NewGuid(), GrantId = s.Barrier.GrantId,
            RepositoryId = s.Barrier.RepositoryId,
            State = exact
                ? GitHubRemoteReadbackStates.ExactTipEvidenceOnly
                : GitHubRemoteReadbackStates.OperatorReviewRequired,
            BaselineSha256 = hazard.BaselineSha256,
            CandidateSetSha256 = s.Barrier.CandidateSetSha256,
            RefSetSha256 = Sha('1'), TipEvidenceSha256 = Sha('2'),
            AssessmentSha256 = Sha('3'), JournalEvidenceSha256 = Sha('4'),
            ObservedBy = Guid.NewGuid(),
            ObservedAtUtc = after, RecordedAtUtc = after,
            TransportAttempted = false, RemoteWritesAuthorized = false,
            MayAutomaticallyRetry = false
        };
        db.GitHubRemoteReadbacks.Add(receipt);
        if (includeAudit)
            db.GitHubRemoteReadbackEvents.Add(new()
            {
                ReadbackId = receipt.Id, Type = "Recorded",
                ActorId = receipt.ObservedBy, OccurredAtUtc = after
            });
        await db.SaveChangesAsync();
        return receipt;
    }

    private static async Task<GitHubRemoteObjectReconciliationRecord>
        InsertClosureAsync(AppDbContext db, Scenario s,
            GitHubRemoteReadbackRecord readback,
            bool corruptDigest = false)
    {
        var after = DateTimeOffset.UtcNow;
        var raw = new GitHubRemoteObjectReconciliationStatus(
            Guid.NewGuid(), s.Barrier.GrantId, readback.Id,
            s.Barrier.HazardId, s.Barrier.RepositoryId,
            Guid.NewGuid(),
            GitHubRemoteObjectReconciliationStates.FullCopyEvidenceOnly,
            readback.BaselineSha256, readback.CandidateSetSha256,
            readback.RefSetSha256, Sha('5'), Sha('6'), Sha('7'), "",
            Guid.NewGuid(), after, after, false, false, false, false);
        var digest = GitHubRemotePostSendRecoveryIntegrity
            .FullClosureDigest(raw);
        var record = new GitHubRemoteObjectReconciliationRecord
        {
            Id = raw.Id, GrantId = raw.GrantId,
            ReadbackId = raw.ReadbackId, HazardId = raw.HazardId,
            RepositoryId = raw.RepositoryId,
            SourceSnapshotId = raw.SourceSnapshotId,
            State = raw.State,
            OriginalBaselineSha256 = raw.OriginalBaselineSha256,
            CandidateSetSha256 = raw.CandidateSetSha256,
            TargetRefSetSha256 = raw.TargetRefSetSha256,
            RemoteRefObservationSha256 = raw.RemoteRefObservationSha256,
            FullObjectClosureSha256 = raw.FullObjectClosureSha256,
            RemoteMirrorReceiptSha256 = raw.RemoteMirrorReceiptSha256,
            EvidenceSha256 = corruptDigest ? Sha('f') : digest,
            ObservedBy = raw.ObservedBy, VerifiedAtUtc = after,
            RecordedAtUtc = after, RemotePushInvoked = false,
            RemoteWritesAuthorized = false,
            MayAutomaticallyRetry = false, MigrationComplete = false
        };
        db.GitHubRemoteObjectReconciliations.Add(record);
        db.GitHubRemoteObjectReconciliationEvents.Add(new()
        {
            ReconciliationId = record.Id, Type = "Recorded",
            ActorId = record.ObservedBy, OccurredAtUtc = after
        });
        await db.SaveChangesAsync();
        return record;
    }

    [Fact]
    public async Task F085_after_F082_or_crash_always_projects_pending_without_retry()
    {
        Scenario s;
        await using (var writer = fixture.NewContext())
            s = await SeedAsync(writer);
        await using var restarted = fixture.NewContext();
        var inspection = await InspectAsync(restarted, s);
        Assert.NotNull(inspection);
        Assert.Equal(GitHubRemotePostSendRecoveryStates.AwaitingIndependentReadback,
            inspection.State);
        Assert.Equal(s.Barrier.EvidenceSha256,
            inspection.BarrierEvidenceSha256);
        Assert.Null(inspection.ReadbackId);
        Assert.Null(inspection.ReconciliationId);
        Assert.Null(inspection.Checkpoint);
        Assert.False(inspection.CanRetry);
        Assert.False(inspection.CanDispatch);
        Assert.False(inspection.MigrationComplete);
        Assert.Empty(await restarted.GitHubRemotePostSendClosureCheckpoints
            .AsNoTracking().Where(x => x.BarrierId == s.Barrier.Id)
            .ToArrayAsync());
        Assert.Null(await Journal(restarted, s).InspectScopedAsync(
            s.Barrier.Id, s.Barrier.ConfirmationId, s.Barrier.IntentId,
            s.Packet.PreparationId, Guid.NewGuid(), s.Packet.ScopeId, default));
        Assert.Equal("PostSendIndependentClosureMissing",
            (await Assert.ThrowsAsync<GitHubRemotePostSendRecoveryException>(
                () => CheckpointAsync(restarted, s, Guid.NewGuid()))).Code);
    }

    [Fact]
    public async Task F085_independent_tips_then_full_closure_then_immutable_checkpoint()
    {
        var db = fixture.NewContext();
        await using (db)
        {
            var s = await SeedAsync(db);
            var tips = await InsertTipsAsync(db, s);
            var tipState = await InspectAsync(db, s);
            Assert.NotNull(tipState);
            Assert.Equal(GitHubRemotePostSendRecoveryStates.TipsObservedNeedsClosure,
                tipState.State);
            Assert.Equal(tips.Id, tipState.ReadbackId);
            Assert.Null(tipState.ReconciliationId);
            Assert.False(tipState.CanRetry);
            Assert.Equal("PostSendIndependentClosureMissing",
                (await Assert.ThrowsAsync<GitHubRemotePostSendRecoveryException>(
                    () => CheckpointAsync(db, s, Guid.NewGuid()))).Code);

            var proof = await InsertClosureAsync(db, s, tips);
            var full = await InspectAsync(db, s);
            Assert.NotNull(full);
            Assert.Equal(GitHubRemotePostSendRecoveryStates.IndependentClosureObserved,
                full.State);
            Assert.Equal(proof.Id, full.ReconciliationId);
            Assert.Equal(proof.FullObjectClosureSha256,
                full.FullObjectClosureSha256);
            var actor = Guid.NewGuid();
            var checkpoint = await CheckpointAsync(db, s, actor);
            GitHubRemotePostSendRecoveryIntegrity.VerifyCheckpoint(checkpoint);
            Assert.Equal(proof.EvidenceSha256,
                checkpoint.ReconciliationEvidenceSha256);
            Assert.Equal(tips.JournalEvidenceSha256,
                checkpoint.ReadbackEvidenceSha256);
            Assert.Equal(s.Barrier.EvidenceSha256,
                checkpoint.BarrierEvidenceSha256);
            Assert.False(checkpoint.CanRetry);
            Assert.False(checkpoint.CanDispatch);
            Assert.False(checkpoint.MigrationComplete);

            var again = await CheckpointAsync(db, s, actor);
            Assert.Equal(checkpoint.Id, again.Id);
            Assert.Equal("PostSendCheckpointConflict",
                (await Assert.ThrowsAsync<GitHubRemotePostSendRecoveryException>(
                    () => CheckpointAsync(db, s, Guid.NewGuid()))).Code);

            await using var recovered = fixture.NewContext();
            var recorded = await InspectAsync(recovered, s);
            Assert.NotNull(recorded);
            Assert.Equal(
                GitHubRemotePostSendRecoveryStates.CheckpointedIndependentClosure,
                recorded.State);
            Assert.Equal(checkpoint.Id, recorded.Checkpoint!.Id);
            Assert.False(recorded.CanRetry);
            Assert.False(recorded.CanDispatch);
            Assert.False(recorded.MigrationComplete);
            Assert.Single(await recovered.GitHubRemotePostSendClosureEvents
                .AsNoTracking().Where(x => x.CheckpointId == checkpoint.Id)
                .ToArrayAsync());
        }
    }

    [Fact]
    public async Task F085_six_replicas_write_one_checkpoint_and_one_audit()
    {
        Scenario s;
        await using (var db = fixture.NewContext())
        {
            s = await SeedAsync(db);
            var tips = await InsertTipsAsync(db, s);
            await InsertClosureAsync(db, s, tips);
        }
        var actor = Guid.NewGuid();
        async Task<GitHubRemotePostSendClosureCheckpoint> Record()
        {
            await using var db = fixture.NewContext();
            return await CheckpointAsync(db, s, actor);
        }
        var all = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => Record()));
        Assert.All(all, r =>
        {
            Assert.Equal(all[0].Id, r.Id);
            Assert.False(r.CanRetry);
            Assert.False(r.MigrationComplete);
        });
        await using var audit = fixture.NewContext();
        Assert.Single(await audit.GitHubRemotePostSendClosureCheckpoints
            .AsNoTracking().Where(x => x.BarrierId == s.Barrier.Id)
            .ToArrayAsync());
        Assert.Single(await audit.GitHubRemotePostSendClosureEvents
            .AsNoTracking().Where(x => x.CheckpointId == all[0].Id)
            .ToArrayAsync());
    }

    [Fact]
    public async Task F085_bad_F054_audit_or_F058_sha_blocks_checkpoint_and_read()
    {
        await using var noAudit = fixture.NewContext();
        var s = await SeedAsync(noAudit);
        await InsertTipsAsync(noAudit, s, includeAudit: false);
        Assert.Equal("PostSendReadbackHistoryCorrupt",
            (await Assert.ThrowsAsync<GitHubRemotePostSendRecoveryException>(
                () => InspectAsync(noAudit, s))).Code);
        Assert.Empty(await noAudit.GitHubRemotePostSendClosureCheckpoints
            .AsNoTracking().Where(x => x.BarrierId == s.Barrier.Id)
            .ToArrayAsync());

        await using var badClosure = fixture.NewContext();
        var other = await SeedAsync(badClosure);
        var tips = await InsertTipsAsync(badClosure, other);
        await InsertClosureAsync(badClosure, other, tips, corruptDigest: true);
        Assert.Equal("PostSendFullClosureHistoryCorrupt",
            (await Assert.ThrowsAsync<GitHubRemotePostSendRecoveryException>(
                () => InspectAsync(badClosure, other))).Code);
        Assert.Empty(await badClosure.GitHubRemotePostSendClosureCheckpoints
            .AsNoTracking().Where(x => x.BarrierId == other.Barrier.Id)
            .ToArrayAsync());
    }

    [Fact]
    public async Task F085_operator_review_wrong_actor_and_sql_mutation_fail_closed()
    {
        await using var db = fixture.NewContext();
        var s = await SeedAsync(db);
        await InsertTipsAsync(db, s, exact: false);
        var state = await InspectAsync(db, s);
        Assert.NotNull(state);
        Assert.Equal(GitHubRemotePostSendRecoveryStates.OperatorReviewRequired,
            state.State);
        Assert.Equal("PostSendIndependentClosureMissing",
            (await Assert.ThrowsAsync<GitHubRemotePostSendRecoveryException>(
                () => CheckpointAsync(db, s, Guid.NewGuid()))).Code);

        await using var otherDb = fixture.NewContext();
        var other = await SeedAsync(otherDb);
        var tips = await InsertTipsAsync(otherDb, other);
        await InsertClosureAsync(otherDb, other, tips);
        Assert.Equal("PostSendIndependentObserverRequired",
            (await Assert.ThrowsAsync<GitHubRemotePostSendRecoveryException>(
                () => CheckpointAsync(otherDb, other, other.Barrier.MarkedBy))).Code);
        var checkpoint = await CheckpointAsync(otherDb, other, Guid.NewGuid());
        foreach (var query in new[] {
            $"UPDATE github_remote_post_send_closure_checkpoints SET can_retry=true WHERE id='{checkpoint.Id}'",
            $"UPDATE github_remote_post_send_closure_checkpoints SET evidence_sha256='{Sha('f')}' WHERE id='{checkpoint.Id}'",
            $"DELETE FROM github_remote_post_send_closure_checkpoints WHERE id='{checkpoint.Id}'",
            $"DELETE FROM github_remote_post_send_closure_events WHERE checkpoint_id='{checkpoint.Id}'"
        })
            await Assert.ThrowsAnyAsync<Exception>(() =>
                otherDb.Database.ExecuteSqlRawAsync(query));
        Assert.Equal(checkpoint.EvidenceSha256,
            (await InspectAsync(otherDb, other))!.Checkpoint!.EvidenceSha256);
    }

    [Fact]
    public async Task F085_disabled_config_rejects_even_previously_recorded_history()
    {
        await using var db = fixture.NewContext();
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        var disabled = new PostgresGitHubRemotePostSendRecoveryJournal(
            db, new(false, e.RepositoryId, "gh-org/sandbox"),
            PostgresGitHubRemoteSandboxPreSendBarrierTests.Journal(db, e));
        var error = await Assert.ThrowsAsync<GitHubRemotePostSendRecoveryException>(
            () => disabled.InspectScopedAsync(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), default));
        Assert.Equal("PostSendRecoveryDisabled", error.Code);
    }
}
