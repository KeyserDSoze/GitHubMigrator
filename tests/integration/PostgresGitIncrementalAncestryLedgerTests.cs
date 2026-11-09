using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Real PostgreSQL 17 tests of F-095 durability/replay/trigger guards.
// Physical F-094 Git subprocesses are independently tested in
// LocalGitIncrementalAncestryAuditorTests. The fake F-032/F-094 ports
// here isolate the DB proof-join and exactly-once admission contract.
public sealed class PostgresGitIncrementalAncestryLedgerTests(
    PostgresDraftFixture fixture) : IClassFixture<PostgresDraftFixture>,
    IAsyncLifetime
{
    private static readonly string Oid = new('a', 40);

    public async Task InitializeAsync()
    {
        using var services = new ServiceCollection()
            .AddDbContext<AppDbContext>(
                o => o.UseNpgsql(fixture.ConnectionString))
            .BuildServiceProvider();
        await GitHubSourceSyncCheckpointSchema.InitializeAsync(services);
        await GitIncrementalAncestryLedgerSchema.InitializeAsync(services);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static PostgresGitIncrementalAncestryLedger Ledger(
        AppDbContext db,
        PostgresIncrementalOperationPlanTests.ManualClock clock,
        bool enabled = true,
        IVerifiedSourceGitRefReader? reader = null,
        ILocalGitIncrementalAncestryAuditor? physical = null) =>
        new(db, clock, new(enabled),
            new PostgresIncrementalOperationPlanRepository(db, clock),
            Checkpoints(db, clock),
            reader ?? new TestedSourceReader(),
            physical ?? new StubOfflineAuditor());

    private static PostgresGitHubSourceSyncCheckpointJournal Checkpoints(
        AppDbContext db,
        PostgresIncrementalOperationPlanTests.ManualClock clock) =>
        new(db, clock, new(true),
            new PostgresIncrementalOperationPlanRepository(db, clock),
            new PostgresStagingRepository(db, clock));

    private static async Task<(
        PostgresIncrementalOperationPlanTests.Seed Seed,
        IncrementalOperationPlan Plan, GitHubSourceSyncCheckpointReceipt Checkpoint,
        GitHubTargetRefEvidenceView Target)> SetupAsync(
        AppDbContext db,
        PostgresIncrementalOperationPlanTests.ManualClock clock)
    {
        var seed = await PostgresIncrementalOperationPlanTests.SeedAsync(
            db, clock, refs: 1);
        var plan = await PostgresIncrementalOperationPlanTests.NewService(
            db, clock).CreateAsync(seed.Run.Id, seed.OperatorId,
                seed.Preflight.EvidenceSha256, default);
        var checkpoint = await Checkpoints(db, clock).CaptureAsync(
            seed.Run.Id, plan.Id, plan.SnapshotId, null,
            plan.ManifestSha256, Guid.NewGuid(), default);
        Assert.Equal(2, checkpoint.EvidenceVersion);
        clock.Advance(TimeSpan.FromSeconds(2));
        var observation = GitHubRefObservationIntegrity.Seal(
            plan.Organization, "migration-one", clock.GetUtcNow(),
            [new("refs/heads/b-0000", Oid, "commit")]);
        var destination = await db.Connections.AsNoTracking()
            .SingleAsync(x => x.Id == plan.DestinationConnectionId);
        var target = await new PostgresGitHubTargetRefEvidenceStore(db, clock)
            .SaveAsync(plan, "repo-1", Guid.NewGuid(),
                destination.ValidatedAtUtc, observation, default);
        Assert.True(target.IsCurrent);
        return (seed, plan, checkpoint, target);
    }

    private static Task<GitIncrementalAncestryLedgerEntry> CaptureAsync(
        PostgresGitIncrementalAncestryLedger ledger,
        PostgresIncrementalOperationPlanTests.Seed seed,
        IncrementalOperationPlan plan,
        GitHubSourceSyncCheckpointReceipt checkpoint,
        GitHubTargetRefEvidenceView target,
        Guid sandboxId, Guid? actorId = null) =>
        ledger.AuditAndRecordAsync(seed.Run.Id, plan.Id, checkpoint.Id,
            target.Id, "repo-1", target.EvidenceSha256,
            sandboxId, actorId ?? Guid.NewGuid(), default);

    [Fact]
    public async Task F095_db_replay_returns_one_immutable_sha_bound_receipt_and_event()
    {
        var clock = new PostgresIncrementalOperationPlanTests.ManualClock(
            DateTimeOffset.UtcNow);
        PostgresIncrementalOperationPlanTests.Seed seed;
        IncrementalOperationPlan plan;
        GitHubSourceSyncCheckpointReceipt checkpoint;
        GitHubTargetRefEvidenceView target;
        await using (var setup = fixture.NewContext())
            (seed, plan, checkpoint, target) = await SetupAsync(setup, clock);

        var sandbox = Guid.NewGuid();
        var actor = Guid.NewGuid();
        GitIncrementalAncestryLedgerEntry first;
        await using (var db = fixture.NewContext())
        {
            var ledger = Ledger(db, clock);
            first = await CaptureAsync(ledger, seed, plan,
                checkpoint, target, sandbox, actor);
            Assert.Equal(checkpoint.Id, first.Receipt.Request.SourceCheckpointId);
            Assert.Equal(target.Id, first.Receipt.Request.TargetObservationId);
            Assert.Equal(checkpoint.RefChangeAttestationSha256,
                first.Receipt.Request.SourceCheckpointAttestationSha256);
            Assert.Equal(target.EvidenceSha256,
                first.Receipt.Request.TargetObservationSha256);
            Assert.Equal(plan.ManifestSha256, first.OperationManifestSha256);
            Assert.Equal("MatchingObjectId",
                Assert.Single(first.Receipt.Items).Status.ToString());
            Assert.False(first.CanPush || first.CanRetry ||
                first.CanDispatch || first.MigrationComplete);
            GitIncrementalAncestryIntegrity.VerifyReceipt(first.Receipt);

            var replay = await CaptureAsync(ledger, seed, plan,
                checkpoint, target, sandbox);
            Assert.Equal(first.Id, replay.Id);
            Assert.Equal(first.Receipt.EvidenceSha256,
                replay.Receipt.EvidenceSha256);
        }

        await using (var db = fixture.NewContext())
        {
            var ledger = Ledger(db, clock);
            var reloaded = await ledger.GetAsync(seed.Run.Id, plan.Id,
                first.Id, default);
            Assert.NotNull(reloaded);
            Assert.Equal(first.Id, reloaded.Id);
            Assert.Equal(first.Receipt.EvidenceSha256,
                reloaded.Receipt.EvidenceSha256);
            Assert.Equal(first.Receipt.Items.ToArray(),
                reloaded.Receipt.Items.ToArray());
            Assert.Equal(1, await db.GitIncrementalAncestryLedgers
                .AsNoTracking().CountAsync(x => x.Id == first.Id));
            Assert.Equal(1, await db.GitIncrementalAncestryLedgerEvents
                .AsNoTracking().CountAsync(x => x.LedgerId == first.Id));
        }

        clock.Advance(TimeSpan.FromMinutes(6));
        await using (var db = fixture.NewContext())
        {
            // Historic receipt remains readable but no fresh audit can be
            // recorded with an expired F-031 observation.
            Assert.NotNull(await Ledger(db, clock).GetAsync(
                seed.Run.Id, plan.Id, first.Id, default));
            var stale = await Assert.ThrowsAsync<
                GitIncrementalAncestryLedgerException>(() =>
                CaptureAsync(Ledger(db, clock), seed, plan,
                    checkpoint, target, Guid.NewGuid()));
            Assert.Contains(stale.Code, new[] {
                "GitAncestryLedgerTargetEvidenceStale",
                "GitAncestryLedgerPlanStale"
            });
        }
    }

    [Fact]
    public async Task F095_six_concurrent_replicas_admit_exactly_one_receipt_and_event()
    {
        var clock = new PostgresIncrementalOperationPlanTests.ManualClock(
            DateTimeOffset.UtcNow);
        PostgresIncrementalOperationPlanTests.Seed seed;
        IncrementalOperationPlan plan;
        GitHubSourceSyncCheckpointReceipt checkpoint;
        GitHubTargetRefEvidenceView target;
        await using (var setup = fixture.NewContext())
            (seed, plan, checkpoint, target) = await SetupAsync(setup, clock);

        var sandboxId = Guid.NewGuid();
        var contexts = Enumerable.Range(0, 6)
            .Select(_ => fixture.NewContext()).ToArray();
        try
        {
            var all = await Task.WhenAll(contexts.Select(db =>
                CaptureAsync(Ledger(db, clock), seed, plan, checkpoint,
                    target, sandboxId, Guid.NewGuid())));
            Assert.Single(all.Select(x => x.Id).Distinct());
            Assert.Single(all.Select(x => x.Receipt.EvidenceSha256).Distinct());
            Assert.All(all, x => Assert.False(x.CanPush ||
                x.CanRetry || x.CanDispatch || x.MigrationComplete));

            await using var verify = fixture.NewContext();
            Assert.Equal(1, await verify.GitIncrementalAncestryLedgers
                .AsNoTracking().CountAsync(x => x.PlanId == plan.Id));
            Assert.Equal(1, await verify.GitIncrementalAncestryLedgerEvents
                .AsNoTracking().CountAsync(x => x.LedgerId == all[0].Id));
        }
        finally
        {
            foreach (var db in contexts)
                await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task F095_same_target_proof_with_new_physical_audit_refuses_conflicting_replay()
    {
        var clock = new PostgresIncrementalOperationPlanTests.ManualClock(
            DateTimeOffset.UtcNow);
        await using var db = fixture.NewContext();
        var (seed, plan, checkpoint, target) = await SetupAsync(db, clock);
        var id = Guid.NewGuid();
        var first = await CaptureAsync(Ledger(db, clock), seed,
            plan, checkpoint, target, id);
        var failure = await Assert.ThrowsAsync<
            GitIncrementalAncestryLedgerException>(() =>
            CaptureAsync(Ledger(db, clock), seed,
                plan, checkpoint, target, Guid.NewGuid()));
        Assert.Equal("GitAncestryLedgerReplayConflict", failure.Code);
        Assert.Single(await db.GitIncrementalAncestryLedgers.AsNoTracking()
            .Where(x => x.Id == first.Id).ToArrayAsync());
    }

    [Fact]
    public async Task F095_db_enforces_false_authorities_and_immutable_receipt_event()
    {
        var clock = new PostgresIncrementalOperationPlanTests.ManualClock(
            DateTimeOffset.UtcNow);
        await using var db = fixture.NewContext();
        var (seed, plan, checkpoint, target) = await SetupAsync(db, clock);
        var first = await CaptureAsync(Ledger(db, clock),
            seed, plan, checkpoint, target, Guid.NewGuid());

        await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE git_incremental_ancestry_ledgers
                SET receipt_sha256 = {new string('0', 64)}
                WHERE id = {first.Id}
                """));
        await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM git_incremental_ancestry_ledger_events
                WHERE ledger_id = {first.Id}
                """));
        await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO git_incremental_ancestry_ledgers (
                    id, preparation_id, plan_id, checkpoint_id,
                    target_observation_id, source_repository_id,
                    target_repository_name, operation_manifest_sha256,
                    source_evidence_sha256, source_checkpoint_attestation_sha256,
                    target_evidence_sha256, receipt_sha256, receipt_json,
                    recorded_by, recorded_at_utc, can_push)
                SELECT {Guid.NewGuid()}, preparation_id, plan_id, checkpoint_id,
                    target_observation_id, source_repository_id,
                    target_repository_name, operation_manifest_sha256,
                    source_evidence_sha256, source_checkpoint_attestation_sha256,
                    target_evidence_sha256, receipt_sha256, receipt_json,
                    recorded_by, recorded_at_utc, true
                FROM git_incremental_ancestry_ledgers WHERE id = {first.Id}
                """));
        await using var read = fixture.NewContext();
        Assert.Equal(first.Id, (await Ledger(read, clock).GetAsync(
            seed.Run.Id, plan.Id, first.Id, default))!.Id);
    }

    [Fact]
    public async Task F095_disabled_foreign_and_partial_inline_ref_evidence_fail_closed()
    {
        var clock = new PostgresIncrementalOperationPlanTests.ManualClock(
            DateTimeOffset.UtcNow);
        await using var db = fixture.NewContext();
        var (seed, plan, checkpoint, target) = await SetupAsync(db, clock);
        Assert.Equal("GitAncestryLedgerDisabled",
            (await Assert.ThrowsAsync<GitIncrementalAncestryLedgerException>(
                () => CaptureAsync(Ledger(db, clock, false), seed,
                    plan, checkpoint, target, Guid.NewGuid()))).Code);

        var wrong = await Assert.ThrowsAsync<
            GitIncrementalAncestryLedgerException>(() =>
            Ledger(db, clock).AuditAndRecordAsync(
                seed.Run.Id, plan.Id, checkpoint.Id, target.Id,
                "repo-1", new string('d', 64),
                Guid.NewGuid(), Guid.NewGuid(), default));
        Assert.Equal("GitAncestryLedgerTargetEvidenceStale", wrong.Code);

        var realSourceReader = new PostgresVerifiedSourceGitRefReader(
            db, new PostgresDiscoveryRefManifestStore(db, clock));
        var partial = await Assert.ThrowsAsync<
            GitIncrementalAncestryLedgerException>(() =>
            CaptureAsync(Ledger(db, clock,
                reader: realSourceReader), seed, plan,
                checkpoint, target, Guid.NewGuid()));
        Assert.Equal("GitAncestryLedgerSourceEvidenceUnavailable",
            partial.Code);
        Assert.Empty(await db.GitIncrementalAncestryLedgers
            .AsNoTracking().Where(x => x.PlanId == plan.Id)
            .ToArrayAsync());
    }

    private sealed class TestedSourceReader : IVerifiedSourceGitRefReader
    {
        public Task<VerifiedSourceGitRefs> ReadAsync(
            IncrementalOperationPlan plan, string repositoryId,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (repositoryId != "repo-1")
                throw new IncrementalGitRefReconciliationException(
                    "SourceRefSnapshotUnavailable");
            // This stub represents an F-014-verified immutable ref list.
            // The real reader is separately tested and rejects partial
            // inline snapshots as shown in the fail-closed test above.
            return Task.FromResult(VerifiedSourceGitRefIntegrity.Seal(
                plan.SnapshotId, plan.SourceConnectionId, repositoryId,
                new string('c', 64),
                [new("refs/heads/b-0000", Oid)]));
        }
    }

    private sealed class StubOfflineAuditor : ILocalGitIncrementalAncestryAuditor
    {
        public Task<GitIncrementalAncestryReceipt> AuditAsync(
            GitIncrementalAncestryRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal(request.ExpectedSourceRefs.ToArray(),
                request.ExpectedTargetRefs.ToArray());
            var item = new GitIncrementalAncestryItem(
                "refs/heads/b-0000", Oid, Oid,
                GitIncrementalAncestryStatus.MatchingObjectId);
            return Task.FromResult(GitIncrementalAncestryIntegrity.Seal(
                request, "sha1",
                new string('d', 64), new string('e', 64),
                2, 2, [item], DateTimeOffset.UtcNow));
        }
    }
}
