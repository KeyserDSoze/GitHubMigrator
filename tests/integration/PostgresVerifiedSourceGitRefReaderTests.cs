using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// F-032 read boundary is exercised with real F-008 leased jobs, F-014
// checkpoint observations/chunks and committed F-013 PostgreSQL staging.
public sealed class PostgresVerifiedSourceGitRefReaderTests(PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>
{
    [Fact]
    public async Task Complete_F014_chunks_bind_275_source_refs_to_original_snapshot_not_mutable_catalog()
    {
        await using var db = fixture.NewContext();
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var sourceId = Guid.NewGuid();
        var jobs = new PostgresMigrationJobRepository(db, clock);
        var job = await jobs.EnqueueDiscoveryAsync(
            sourceId, Guid.NewGuid(), Guid.NewGuid(), default);
        // Other integration fixtures can enqueue unrelated jobs concurrently.
        // Claim only this source so the F-014 proof test never consumes their lease.
        var lease = (await jobs.ClaimForSourceAsync(
            sourceId, Guid.NewGuid(), TimeSpan.FromMinutes(3), default))!;
        Assert.Equal(job.Id, lease.Id);
        var now = clock.GetUtcNow();
        var refs = Enumerable.Range(0, 275).Select(i => new RepositoryRef(
            "repo-1", $"refs/heads/topic-{i:0000}", i.ToString("x40"))).ToArray();
        var inventory = new DiscoveryInventory {
            Id = job.Id, ConnectionId = sourceId,
            SourceProvider = "azure-devops-services", Organization = "az-org",
            CollectedAtUtc = now,
            Snapshot = new DiscoverySnapshot {
                SnapshotId = job.Id, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = now,
                Projects = [new SourceProject("project", "Project", [
                    new SourceRepository("repo-1", "project", "One", VersionControlKind.Git)
                ])], Identities = [], AccessGrants = []
            },
            Refs = [], RefsExternalized = true, RefCount = refs.Length,
            Issues = [], Completeness = DiscoveryCompleteness.Partial,
            IdentitiesComplete = false, PermissionsComplete = false
        };
        await new PostgresDiscoveryRepository(db).SaveAsync(inventory, default);
        Assert.True(await jobs.CheckpointSnapshotAsync(lease, job.Id, default));

        var pages = new PostgresDiscoveryPageCheckpointStore(db, clock);
        await pages.SaveAsync(new(lease.Id, lease.LeaseToken, lease.Attempt),
            new DiscoveryPageCheckpoint(job.Id, "refs:repo-1", 0,
                new string('a', 64), JsonSerializer.Serialize(new {
                    value = refs.Select(x => new { name = x.Name, objectId = x.ObjectId })
                }), null, now), default);
        var manifests = new PostgresDiscoveryRefManifestStore(db, clock);
        var prepared = await manifests.PrepareAsync(lease, refs.Length, default);
        Assert.Equal(2, prepared.ChunkCount);

        var applied = await new StagingSyncService(
            new PostgresDiscoveryRepository(db),
            new PostgresStagingRepository(db, clock, manifests))
                .SyncUsingRefManifestAsync(lease with { SnapshotId = lease.Id }, default);
        Assert.Equal(277, applied.Observed);
        Assert.True(await jobs.CompleteAsync(lease, applied, default));

        var plan = FakePlan(job.Id, sourceId);
        var sourceReader = new PostgresVerifiedSourceGitRefReader(db, manifests);
        var sealedProof = await sourceReader.ReadAsync(plan, "repo-1", default);
        Assert.Equal(275, sealedProof.Refs.Count);
        Assert.Equal(prepared.RootSha256, sealedProof.RefManifestRootSha256);
        Assert.Equal(64, sealedProof.SourceEvidenceSha256.Length);
        Assert.Equal("refs/heads/topic-0000", sealedProof.Refs[0].Name);
        Assert.Equal("refs/heads/topic-0274", sealedProof.Refs[^1].Name);
        Assert.Equal(sealedProof.SourceEvidenceSha256,
            (await sourceReader.ReadAsync(plan, "repo-1", default)).SourceEvidenceSha256);

        // The mutable latest catalog is NOT used as F-032 source evidence.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE staging_assets SET payload_json = {"{\"ObjectId\":\"corrupted\"}"}::jsonb
            WHERE source_connection_id = {sourceId} AND asset_type = {"ref"}
            """);
        Assert.Equal(sealedProof.SourceEvidenceSha256,
            (await sourceReader.ReadAsync(plan, "repo-1", default)).SourceEvidenceSha256);

        // Modifying even one F-014 observation without updating the chunk
        // hash causes source verification to fail before any compare is stored.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE discovery_ref_observations SET fingerprint = {"tampered"}
            WHERE job_id = {job.Id} AND source_id = {StagingAssetNormalizer.NormalizeRef(
                "repo-1", "refs/heads/topic-0000", refs[0].ObjectId).SourceId}
            """);
        var failure = await Assert.ThrowsAsync<DiscoveryRefManifestException>(() =>
            sourceReader.ReadAsync(plan, "repo-1", default));
        Assert.Equal("RefManifestIntegrityFailed", failure.Code);
    }

    [Fact]
    public async Task Partial_inline_snapshots_cannot_claim_target_only_refs_are_absent()
    {
        await using var db = fixture.NewContext();
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var sourceId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var inventory = new DiscoveryInventory {
            Id = snapshotId, ConnectionId = sourceId,
            SourceProvider = "azure-devops-services", Organization = "az-org",
            CollectedAtUtc = clock.GetUtcNow(),
            Snapshot = new DiscoverySnapshot {
                SnapshotId = snapshotId, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = clock.GetUtcNow(),
                Projects = [new SourceProject("project", "Project", [
                    new SourceRepository("repo-1", "project", "One", VersionControlKind.Git)
                ])], Identities = [], AccessGrants = []
            },
            Refs = [new RepositoryRef("repo-1", "refs/heads/main",
                new string('a', 40))],
            Issues = [], Completeness = DiscoveryCompleteness.Partial,
            IdentitiesComplete = false, PermissionsComplete = false
        };
        await new PostgresDiscoveryRepository(db).SaveAsync(inventory, default);
        var normalized = StagingAssetNormalizer.Normalize(inventory);
        await new PostgresStagingRepository(db, clock)
            .ApplyAsync(inventory, normalized.Assets, normalized.CompleteTypes, default);
        var reader = new PostgresVerifiedSourceGitRefReader(db,
            new PostgresDiscoveryRefManifestStore(db, clock));
        var failure = await Assert.ThrowsAsync<IncrementalGitRefReconciliationException>(() =>
            reader.ReadAsync(FakePlan(snapshotId, sourceId), "repo-1", default));
        Assert.Equal("SourceRefCoverageUnverified", failure.Code);
    }


    [Fact]
    public async Task F097_later_complete_F014_snapshot_is_not_the_F029_baseline_or_mutable_catalog()
    {
        await using var db = fixture.NewContext();
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var connection = Guid.NewGuid();
        var jobs = new PostgresMigrationJobRepository(db, clock);
        var job = await jobs.EnqueueDiscoveryAsync(
            connection, Guid.NewGuid(), Guid.NewGuid(), default);
        var lease = (await jobs.ClaimForSourceAsync(
            connection, Guid.NewGuid(), TimeSpan.FromMinutes(3), default))!;
        Assert.Equal(job.Id, lease.Id);

        var now = clock.GetUtcNow();
        var refs = new[] {
            new RepositoryRef("repo-1", "refs/heads/main", new string('a', 40)),
            new RepositoryRef("repo-1", "refs/tags/v1", new string('b', 40))
        };
        var inventory = new DiscoveryInventory {
            Id = job.Id, ConnectionId = connection,
            SourceProvider = "azure-devops-services",
            Organization = "az-org", CollectedAtUtc = now,
            Snapshot = new DiscoverySnapshot {
                SnapshotId = job.Id, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = now,
                Projects = [new SourceProject("project", "Project", [
                    new SourceRepository("repo-1", "project", "One",
                        VersionControlKind.Git)
                ])],
                Identities = [], AccessGrants = []
            },
            Refs = [], RefsExternalized = true, RefCount = refs.Length,
            Issues = [], Completeness = DiscoveryCompleteness.Complete,
            IdentitiesComplete = true, PermissionsComplete = true,
            SecurityCoverage = new DiscoveryCoverage(true, true, true, false)
        };
        await new PostgresDiscoveryRepository(db).SaveAsync(inventory, default);
        Assert.True(await jobs.CheckpointSnapshotAsync(lease, job.Id, default));
        var pages = new PostgresDiscoveryPageCheckpointStore(db, clock);
        await pages.SaveAsync(new(lease.Id, lease.LeaseToken, lease.Attempt),
            new DiscoveryPageCheckpoint(job.Id, "refs:repo-1", 0,
                new string('f', 64), JsonSerializer.Serialize(new {
                    value = refs.Select(x => new { name = x.Name, objectId = x.ObjectId })
                }), null, now), default);
        var manifests = new PostgresDiscoveryRefManifestStore(db, clock);
        await manifests.PrepareAsync(lease, refs.Length, default);
        var reader = new PostgresVerifiedSourceGitRefReader(db, manifests);
        var plan = FakePlan(Guid.NewGuid(), connection) with {
            Steps = [new IncrementalOperationStep(1, null, "repo-1",
                "target", "ReviewTargetName", "Blocked", "Blocked", 0)]
        };

        // Before the worker seals and publishes the source manifest,
        // a historical checkpoint may not consume provisional ref pages.
        var provisional = await Assert.ThrowsAsync<
            IncrementalGitRefReconciliationException>(() =>
            reader.ReadSealedSnapshotAsync(plan, job.Id, "repo-1", default));
        Assert.Equal("SourceRefSnapshotUnavailable", provisional.Code);

        var applied = await new StagingSyncService(
            new PostgresDiscoveryRepository(db),
            new PostgresStagingRepository(db, clock, manifests))
            .SyncUsingRefManifestAsync(
                lease with { SnapshotId = lease.Id }, default);
        Assert.True(await jobs.CompleteAsync(lease, applied, default));
        Assert.True((await db.StagingSnapshotSyncs.AsNoTracking()
            .SingleAsync(x => x.SnapshotId == job.Id)).SourceInventoryComplete);

        var proof = await reader.ReadSealedSnapshotAsync(
            plan, job.Id, "repo-1", default);
        Assert.Equal(job.Id, proof.SnapshotId);
        Assert.NotEqual(plan.SnapshotId, proof.SnapshotId);
        Assert.Equal(2, proof.Refs.Count);
        Assert.Equal(new string('a', 40), proof.Refs[0].ObjectId);
        Assert.NotNull(proof.RefManifestRootSha256);

        // A mutable staging change never alters the historic immutable refs.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE staging_assets SET payload_json = {"{}"}::jsonb
            WHERE source_connection_id = {connection} AND asset_type = {"ref"}
            """);
        Assert.Equal(proof.SourceEvidenceSha256,
            (await reader.ReadSealedSnapshotAsync(
                plan, job.Id, "repo-1", default)).SourceEvidenceSha256);

        await Assert.ThrowsAsync<IncrementalGitRefReconciliationException>(() =>
            reader.ReadSealedSnapshotAsync(plan, job.Id, "foreign", default));
        await Assert.ThrowsAsync<IncrementalGitRefReconciliationException>(() =>
            reader.ReadSealedSnapshotAsync(
                plan, Guid.NewGuid(), "repo-1", default));

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE discovery_ref_observations SET fingerprint = {"tampered"}
            WHERE job_id = {job.Id}
            """);
        await Assert.ThrowsAsync<DiscoveryRefManifestException>(() =>
            reader.ReadSealedSnapshotAsync(plan, job.Id, "repo-1", default));
    }

    [Fact]
    public async Task F097_partial_inline_ref_snapshot_never_passes_full_epoch_reader()
    {
        await using var db = fixture.NewContext();
        var now = DateTimeOffset.UtcNow;
        var clock = new FixedClock(now);
        var connection = Guid.NewGuid();
        var snapshot = Guid.NewGuid();
        var inventory = new DiscoveryInventory {
            Id = snapshot, ConnectionId = connection,
            SourceProvider = "azure-devops-services", Organization = "az-org",
            CollectedAtUtc = now,
            Snapshot = new DiscoverySnapshot {
                SnapshotId = snapshot, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = now, Projects = [new SourceProject("project", "Project", [
                    new SourceRepository("repo-1", "project", "One", VersionControlKind.Git)
                ])], Identities = [], AccessGrants = []
            },
            Refs = [new RepositoryRef(
                "repo-1", "refs/heads/main", new string('a', 40))],
            Issues = [], Completeness = DiscoveryCompleteness.Partial,
            IdentitiesComplete = false, PermissionsComplete = false
        };
        await new PostgresDiscoveryRepository(db).SaveAsync(inventory, default);
        var normalized = StagingAssetNormalizer.Normalize(inventory);
        await new PostgresStagingRepository(db, clock).ApplyAsync(
            inventory, normalized.Assets, normalized.CompleteTypes, default);
        var plan = FakePlan(Guid.NewGuid(), connection) with {
            Steps = [new IncrementalOperationStep(1, null, "repo-1",
                "target", "ReviewTargetName", "Blocked", "Blocked", 0)]
        };
        var reader = new PostgresVerifiedSourceGitRefReader(db,
            new PostgresDiscoveryRefManifestStore(db, clock));
        var error = await Assert.ThrowsAsync<IncrementalGitRefReconciliationException>(
            () => reader.ReadSealedSnapshotAsync(
                plan, snapshot, "repo-1", default));
        Assert.Equal("SourceRefCheckpointCoverageUnverified", error.Code);
    }

    private static IncrementalOperationPlan FakePlan(Guid snapshot, Guid connection) =>
        new(Guid.NewGuid(), Guid.NewGuid(), snapshot, connection, Guid.NewGuid(),
            "gh-org", new string('a', 64), new string('b', 64),
            new string('c', 64), new string('d', 64), DateTimeOffset.UtcNow,
            Guid.NewGuid(), 1, 6, 0, 0, true, false, [], []);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
