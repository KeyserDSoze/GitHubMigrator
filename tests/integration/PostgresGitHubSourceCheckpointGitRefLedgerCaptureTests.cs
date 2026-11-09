using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Real F-008/F-014 -> F-013 -> F-026/029 -> F-090/091 -> F-097b,
// including restart/replay. No stubs for source, plan or checkpoint proofs.
public sealed class PostgresGitHubSourceCheckpointGitRefLedgerCaptureTests(
    PostgresDraftFixture fixture) : IClassFixture<PostgresDraftFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        using var services = new ServiceCollection()
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(fixture.ConnectionString))
            .BuildServiceProvider();
        await GitHubSourceSyncCheckpointSchema.InitializeAsync(services);
        await GitHubSourceCheckpointGitRefLedgerSchema.InitializeAsync(services);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static PostgresGitHubSourceCheckpointGitRefLedger Ledger(
        AppDbContext db, TimeProvider clock) =>
        new(db, clock, new(true),
            new PostgresIncrementalOperationPlanRepository(db, clock),
            new PostgresGitHubSourceSyncCheckpointJournal(
                db, clock, new(true),
                new PostgresIncrementalOperationPlanRepository(db, clock),
                new PostgresStagingRepository(db, clock)),
            new PostgresVerifiedSourceGitRefReader(
                db, new PostgresDiscoveryRefManifestStore(db, clock)));

    private static async Task<(
        IncrementalPreparationView Preparation,
        IncrementalOperationPlan Plan,
        GitHubSourceSyncCheckpointReceipt Checkpoint,
        Guid SourceJobId, Guid ActorId)> SetupAsync(
        AppDbContext db, PostgresIncrementalOperationPlanTests.ManualClock clock)
    {
        // An older staging sync exists on this source. The newly completed
        // durable discovery must become the exact F-029 *baseline* epoch.
        var older = await PostgresIncrementalOperationPlanTests.SeedAsync(
            db, clock, refs: 1);
        var source = older.Run.SourceConnectionId;
        var destination = older.Run.DestinationConnectionId;
        var jobs = new PostgresMigrationJobRepository(db, clock);
        var job = await jobs.EnqueueDiscoveryAsync(
            source, Guid.NewGuid(), Guid.NewGuid(), default);
        var lease = (await jobs.ClaimForSourceAsync(
            source, Guid.NewGuid(), TimeSpan.FromMinutes(3), default))!;
        Assert.Equal(job.Id, lease.Id);
        var observedAt = clock.GetUtcNow().AddMinutes(1);
        var refs = new[] {
            new RepositoryRef("repo-1", "refs/heads/main", new string('a', 40)),
            new RepositoryRef("repo-1", "refs/tags/v1", new string('b', 40))
        };
        var inventory = new DiscoveryInventory
        {
            Id = job.Id, ConnectionId = source,
            SourceProvider = "azure-devops-services",
            Organization = "az-org", CollectedAtUtc = observedAt,
            Snapshot = new DiscoverySnapshot
            {
                SnapshotId = job.Id,
                SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = observedAt,
                Projects = [new SourceProject("project", "Project", [
                    new SourceRepository("repo-1", "project", "One",
                        VersionControlKind.Git),
                    new SourceRepository("repo-2", "project", "Two",
                        VersionControlKind.Git)
                ])],
                Identities = [], AccessGrants = []
            },
            Refs = [], RefsExternalized = true, RefCount = refs.Length,
            Issues = [new DiscoveryIssue("EffectivePermissionsNotVerified")],
            Completeness = DiscoveryCompleteness.Partial,
            IdentitiesComplete = false, PermissionsComplete = false
        };
        await new PostgresDiscoveryRepository(db).SaveAsync(inventory, default);
        Assert.True(await jobs.CheckpointSnapshotAsync(lease, job.Id, default));
        await new PostgresDiscoveryPageCheckpointStore(db, clock).SaveAsync(
            new(lease.Id, lease.LeaseToken, lease.Attempt),
            new DiscoveryPageCheckpoint(job.Id, "refs:repo-1", 0,
                new string('f', 64),
                JsonSerializer.Serialize(new
                {
                    value = refs.Select(x => new
                    {
                        name = x.Name, objectId = x.ObjectId
                    })
                }), null, observedAt), default);
        var manifests = new PostgresDiscoveryRefManifestStore(db, clock);
        Assert.Equal(refs.Length,
            (await manifests.PrepareAsync(lease, refs.Length, default)).AssetCount);
        var staged = await new StagingSyncService(
            new PostgresDiscoveryRepository(db),
            new PostgresStagingRepository(db, clock, manifests))
            .SyncUsingRefManifestAsync(
                lease with { SnapshotId = lease.Id }, default);
        Assert.True(await jobs.CompleteAsync(lease, staged, default));

        var preparations = new PostgresIncrementalPreparationRepository(
            db, clock, useDatabaseClock: false);
        var queued = await preparations.EnqueueAsync(
            job.Id, source, destination, older.OperatorId, default);
        var prepLease = await preparations.ClaimAsync(
            Guid.NewGuid(), TimeSpan.FromMinutes(5), default, source);
        Assert.NotNull(prepLease);
        Assert.True(await preparations.PrepareAsync(prepLease, default));
        var run = (await preparations.GetAsync(queued.Id, default))!;
        var review = new PostgresIncrementalReviewRepository(db, clock);
        var dry = await review.DryRunAsync(run.Id, older.OperatorId, default);
        var approved = await review.DecideAsync(run.Id, older.ReviewerId,
            "ApproveForPlanning", dry.DryRunSha256, false, null, default);

        var now = DateTimeOffset.FromUnixTimeMilliseconds(
            clock.GetUtcNow().ToUnixTimeMilliseconds());
        var preflight = await new PostgresIncrementalTargetPreflightRepository(
            db, clock).SaveAsync(new IncrementalTargetPreflightEvidence(
                run.Id, run.SnapshotId, run.SourceConnectionId,
                run.DestinationConnectionId, Guid.NewGuid(), "gh-org",
                approved.DryRunSha256,
                DateTimeOffset.FromUnixTimeMilliseconds(
                    run.CreatedAtUtc.ToUnixTimeMilliseconds()),
                DateTimeOffset.FromUnixTimeMilliseconds(
                    run.CreatedAtUtc.ToUnixTimeMilliseconds()),
                now, "Active", "Owner", "AllowedForMembers",
                [
                    new("repo-1", "migration-one", "GitMirror", "NotVisibleOrMissing"),
                    new("repo-2", "migration-two", "GitMirror", "NotVisibleOrMissing")
                ]), default);
        Assert.True(preflight.IsCurrent);
        var plan = await PostgresIncrementalOperationPlanTests.NewService(
            db, clock).CreateAsync(
                run.Id, older.OperatorId, preflight.EvidenceSha256, default);
        var checkpoint = await new PostgresGitHubSourceSyncCheckpointJournal(
            db, clock, new(true),
            new PostgresIncrementalOperationPlanRepository(db, clock),
            new PostgresStagingRepository(db, clock)).CaptureAsync(
                run.Id, plan.Id, plan.SnapshotId, null,
                plan.ManifestSha256, older.OperatorId, default);
        Assert.Equal(2, checkpoint.EvidenceVersion);
        Assert.False(checkpoint.Proof.SourceInventoryComplete);
        return (run, plan, checkpoint, job.Id, older.OperatorId);
    }

    [Fact]
    public async Task F097_sealed_F014_checkpoint_survives_restart_and_concurrent_exact_replay()
    {
        var clock = new PostgresIncrementalOperationPlanTests.ManualClock(
            DateTimeOffset.UtcNow);
        IncrementalPreparationView run;
        IncrementalOperationPlan plan;
        GitHubSourceSyncCheckpointReceipt checkpoint;
        Guid job, actor;
        await using (var setup = fixture.NewContext())
        {
            (run, plan, checkpoint, job, actor) = await SetupAsync(setup, clock);
        }

        // Each replica receives the same trusted F-091 checkpoint ID,
        // never an external ref/OID set; PostgreSQL source/plan locks
        // must converge to the same immutable proof.
        var proofs = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            await using var replica = fixture.NewContext();
            return await Ledger(replica, clock).CaptureAsync(
                run.Id, plan.Id, checkpoint.Id, "repo-1", actor, default);
        }));
        Assert.Equal(1, proofs.Select(x => x.EvidenceSha256).Distinct().Count());
        Assert.Equal(job, proofs[0].SnapshotId);
        Assert.Equal(2, proofs[0].Refs.Count);
        Assert.All(proofs, x =>
        {
            Assert.False(x.CanPush);
            Assert.False(x.CanRetry);
            Assert.False(x.CanDispatch);
            Assert.False(x.MigrationComplete);
        });

        await using (var restarted = fixture.NewContext())
        {
            var journal = Ledger(restarted, clock);
            var read = await journal.GetVerifiedAsync(
                run.Id, plan.Id, checkpoint.Id, "repo-1", default);
            Assert.NotNull(read);
            Assert.Equal(proofs[0].EvidenceSha256, read.EvidenceSha256);
            Assert.Equal(1, await restarted.GitHubSourceCheckpointGitRefLedgers
                .AsNoTracking().CountAsync(x => x.CheckpointId == checkpoint.Id));
            var row = await restarted.GitHubSourceCheckpointGitRefLedgers
                .AsNoTracking().SingleAsync(x => x.CheckpointId == checkpoint.Id);
            Assert.Single(await restarted.GitHubSourceCheckpointGitRefLedgerEvents
                .AsNoTracking().Where(x => x.LedgerId == row.Id).ToArrayAsync());
            var conflict = await Assert.ThrowsAsync<
                GitHubSourceCheckpointGitRefLedgerException>(() =>
                    journal.CaptureAsync(run.Id, plan.Id, checkpoint.Id,
                        "repo-1", Guid.NewGuid(), default));
            Assert.Equal("SourceGitRefLedgerReplayConflict", conflict.Code);
        }

        // Independently sealed source rows must remain valid for readback:
        // altering one source fingerprint breaks F-014 and consequently F-097.
        await using (var tampered = fixture.NewContext())
        {
            await tampered.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE discovery_ref_observations
                SET fingerprint = {"tampered"}
                WHERE job_id = {job}
                """);
            var refused = await Assert.ThrowsAsync<
                GitHubSourceCheckpointGitRefLedgerException>(() =>
                    Ledger(tampered, clock).GetVerifiedAsync(
                        run.Id, plan.Id, checkpoint.Id, "repo-1", default));
            Assert.Equal("SourceGitRefLedgerSourceEvidenceInvalid", refused.Code);
        }
    }
}
