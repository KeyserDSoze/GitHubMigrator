using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// F-097b schema and safety tests on real PostgreSQL 17. The positive
// full-F014/F090 end-to-end creation fixture is tracked separately in #94.
public sealed class PostgresGitHubSourceCheckpointGitRefLedgerTests(
    PostgresDraftFixture fixture) : IClassFixture<PostgresDraftFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        using var services = new ServiceCollection()
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(fixture.ConnectionString))
            .BuildServiceProvider();
        await GitHubSourceSyncCheckpointSchema.InitializeAsync(services);
        await GitHubSourceCheckpointGitRefLedgerSchema.InitializeAsync(services);
        // Schema bootstrap must safely run more than once on existing DBs.
        await GitHubSourceCheckpointGitRefLedgerSchema.InitializeAsync(services);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static PostgresGitHubSourceCheckpointGitRefLedger Journal(
        AppDbContext db, TimeProvider clock, bool enabled) =>
        new(db, clock, new(enabled),
            new PostgresIncrementalOperationPlanRepository(db, clock),
            new PostgresGitHubSourceSyncCheckpointJournal(
                db, clock, new(true),
                new PostgresIncrementalOperationPlanRepository(db, clock),
                new PostgresStagingRepository(db, clock)),
            new PostgresVerifiedSourceGitRefReader(
                db, new PostgresDiscoveryRefManifestStore(db, clock)));

    private static async Task<(
        PostgresIncrementalOperationPlanTests.Seed Seed,
        IncrementalOperationPlan Plan,
        GitHubSourceSyncCheckpointReceipt Checkpoint)> SeedAsync(
        AppDbContext db, PostgresIncrementalOperationPlanTests.ManualClock clock)
    {
        var seed = await PostgresIncrementalOperationPlanTests.SeedAsync(
            db, clock, refs: 1);
        var plan = await PostgresIncrementalOperationPlanTests.NewService(
            db, clock).CreateAsync(seed.Run.Id, seed.OperatorId,
                seed.Preflight.EvidenceSha256, default);
        var checkpoint = await new PostgresGitHubSourceSyncCheckpointJournal(
            db, clock, new(true),
            new PostgresIncrementalOperationPlanRepository(db, clock),
            new PostgresStagingRepository(db, clock)).CaptureAsync(
                seed.Run.Id, plan.Id, plan.SnapshotId, null,
                plan.ManifestSha256, seed.OperatorId, default);
        return (seed, plan, checkpoint);
    }

    [Fact]
    public async Task F097_disabled_or_unsealed_legacy_source_can_never_record_full_ref_authority()
    {
        var clock = new PostgresIncrementalOperationPlanTests.ManualClock(
            DateTimeOffset.UtcNow);
        await using var db = fixture.NewContext();
        var (seed, plan, checkpoint) = await SeedAsync(db, clock);
        var disabled = Journal(db, clock, false);
        var error = await Assert.ThrowsAsync<
            GitHubSourceCheckpointGitRefLedgerException>(() =>
                disabled.CaptureAsync(seed.Run.Id, plan.Id, checkpoint.Id,
                    "repo-1", seed.OperatorId, default));
        Assert.Equal("SourceGitRefLedgerDisabled", error.Code);

        var enabled = Journal(db, clock, true);
        Assert.Null(await enabled.GetVerifiedAsync(
            seed.Run.Id, plan.Id, checkpoint.Id, "repo-1", default));
        var invalid = await Assert.ThrowsAsync<
            GitHubSourceCheckpointGitRefLedgerException>(() =>
                enabled.CaptureAsync(seed.Run.Id, plan.Id, checkpoint.Id,
                    "repo-1", seed.OperatorId, default));
        Assert.Equal("SourceGitRefLedgerSourceEvidenceInvalid", invalid.Code);
        Assert.Empty(await db.GitHubSourceCheckpointGitRefLedgers
            .AsNoTracking().Where(x => x.CheckpointId == checkpoint.Id)
            .ToArrayAsync());
        Assert.Empty(await db.GitHubSourceCheckpointGitRefLedgerEvents
            .AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task F097_postgres_receipt_and_event_are_immutable_and_write_flags_are_denied()
    {
        var clock = new PostgresIncrementalOperationPlanTests.ManualClock(
            DateTimeOffset.UtcNow);
        Guid ledgerId;
        await using (var db = fixture.NewContext())
        {
            var (seed, plan, checkpoint) = await SeedAsync(db, clock);
            ledgerId = Guid.NewGuid();
            db.GitHubSourceCheckpointGitRefLedgers.Add(new()
            {
                Id = ledgerId, CheckpointId = checkpoint.Id,
                PlanId = plan.Id, PreparationId = seed.Run.Id,
                SnapshotId = checkpoint.Proof.SnapshotId,
                SourceConnectionId = plan.SourceConnectionId,
                SourceRepositoryId = "repo-1",
                SourceManifestRootSha256 = new string('a', 64),
                EvidenceSha256 = new string('b', 64),
                ProofJson = new string('x', 200),
                RecordedBy = seed.OperatorId, RecordedAtUtc = clock.GetUtcNow(),
                CanPush = false, CanRetry = false,
                CanDispatch = false, MigrationComplete = false
            });
            await db.SaveChangesAsync();
            db.GitHubSourceCheckpointGitRefLedgerEvents.Add(new()
            {
                LedgerId = ledgerId, Type = "Recorded",
                ActorId = seed.OperatorId,
                EvidenceSha256 = new string('b', 64),
                OccurredAtUtc = clock.GetUtcNow()
            });
            await db.SaveChangesAsync();

            // Database constraints prevent authorizing any remote write,
            // even if a buggy caller bypasses the trusted journal.
            var rejectedWrite = await Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO github_checkpoint_source_ref_proofs (
                        id, checkpoint_id, preparation_id, plan_id, snapshot_id,
                        source_connection_id, source_repository_id,
                        source_manifest_root_sha256, proof_json, evidence_sha256,
                        recorded_by, recorded_at_utc,
                        can_push, can_retry, can_dispatch, migration_complete)
                    SELECT {Guid.NewGuid()}, checkpoint_id, preparation_id, plan_id,
                        snapshot_id, source_connection_id, {"another-repo"},
                        source_manifest_root_sha256, proof_json, evidence_sha256,
                        recorded_by, recorded_at_utc,
                        true, false, false, false
                    FROM github_checkpoint_source_ref_proofs WHERE id = {ledgerId}
                    """));
            Assert.Equal(PostgresErrorCodes.CheckViolation, rejectedWrite.SqlState);
        }

        // New context simulates a restart; SQL mutation is rejected before
        // any bogus "migration complete" or hash edit can be persisted.
        await using (var restarted = fixture.NewContext())
        {
            Assert.Single(await restarted.GitHubSourceCheckpointGitRefLedgers
                .AsNoTracking().Where(x => x.Id == ledgerId).ToArrayAsync());
            Assert.Single(await restarted.GitHubSourceCheckpointGitRefLedgerEvents
                .AsNoTracking().Where(x => x.LedgerId == ledgerId).ToArrayAsync());
            var changed = await Assert.ThrowsAsync<PostgresException>(() =>
                restarted.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE github_checkpoint_source_ref_proofs
                    SET evidence_sha256 = {new string('f', 64)}
                    WHERE id = {ledgerId}
                    """));
            Assert.Equal(PostgresErrorCodes.RaiseException, changed.SqlState);
            var deleted = await Assert.ThrowsAsync<PostgresException>(() =>
                restarted.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE FROM github_checkpoint_source_ref_proof_events
                    WHERE ledger_id = {ledgerId}
                    """));
            Assert.Equal(PostgresErrorCodes.RaiseException, deleted.SqlState);
            Assert.Single(await restarted.GitHubSourceCheckpointGitRefLedgerEvents
                .AsNoTracking().Where(x => x.LedgerId == ledgerId).ToArrayAsync());
        }
    }
}
