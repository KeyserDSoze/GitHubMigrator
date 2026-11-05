using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

public sealed class PostgresPlanRefEvidenceTests(PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>
{
    [Fact]
    public async Task Externalized_refs_feed_correct_F005_counts_and_histories_stay_snapshot_bound()
    {
        await using var db = fixture.NewContext();
        var clock = TimeProvider.System;
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var first = await CompletedExternalizedAsync(db, clock, source,
            [Ref("repo-A", "refs/heads/main", "sha1"),
             Ref("repo-A", "refs/tags/one", "sha2"),
             Ref("repo-B", "refs/heads/main", "sha3")]);
        var second = await CompletedExternalizedAsync(db, clock, source,
            [Ref("repo-A", "refs/heads/main", "sha-new")]);

        var now = DateTimeOffset.UtcNow;
        await new PostgresConnectionRepository(db).SaveAsync(
            new MigrationConnection(target, "Target", "github-enterprise-cloud",
                ConnectionDirection.Destination, "gh-org", "no-secret",
                now, now, "Verified", "test", "unverified"), default);
        var plan = new MigrationPlanReviewService(new PostgresDiscoveryRepository(db),
            new PostgresConnectionRepository(db),
            new PostgresIdentityMappingDraftRepository(db),
            new PostgresIdentitySnapshotReader(db),
            new PostgresPlanRefEvidenceReader(db));

        var selected = new[] {
            new PlanSelectionInput("repo-A", "target-a", HistoryStrategy.GitMirror),
            new PlanSelectionInput("repo-B", "target-b", HistoryStrategy.GitMirror)
        };
        var a = await plan.PreviewAsync(first, target, selected, default);
        Assert.Equal(2, a.Repositories.Single(x => x.SourceRepositoryId == "repo-A").RefCount);
        Assert.Equal(1, a.Repositories.Single(x => x.SourceRepositoryId == "repo-B").RefCount);
        Assert.False(a.ReadyForApproval);
        Assert.Contains(a.Checks, x => x.Code == "EffectivePermissionsUnverified");

        var b = await plan.PreviewAsync(second, target, selected, default);
        Assert.Equal(1, b.Repositories.Single(x => x.SourceRepositoryId == "repo-A").RefCount);
        Assert.Equal(0, b.Repositories.Single(x => x.SourceRepositoryId == "repo-B").RefCount);
        Assert.Equal(2, (await plan.PreviewAsync(first, target, selected, default))
            .Repositories.Single(x => x.SourceRepositoryId == "repo-A").RefCount);
    }

    [Fact]
    public async Task Externalized_snapshot_cannot_be_previewed_before_job_success()
    {
        await using var db = fixture.NewContext();
        var source = Guid.NewGuid();
        // A deliberately unfinished job should not be claimable by another
        // test worker in the shared PostgreSQL integration fixture.
        var queuedId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.MigrationJobs.Add(new MigrationJobRecord {
            Id = queuedId, SnapshotId = queuedId, SourceConnectionId = source,
            Kind = MigrationJobKinds.DiscoverSource, State = MigrationJobStates.Running,
            EnqueuedBy = Guid.NewGuid(), IdempotencyKey = "f017-incomplete:" + queuedId,
            Attempts = 1, LeaseToken = Guid.NewGuid(),
            LeaseUntilUtc = now.AddHours(1), AvailableAtUtc = now,
            CreatedAtUtc = now, UpdatedAtUtc = now
        });
        await db.SaveChangesAsync();
        await SaveSnapshotAsync(db, queuedId, source, [],
            externalized: true, expected: 1);
        var reader = new PostgresPlanRefEvidenceReader(db);
        var ex = await Assert.ThrowsAsync<PlanInputException>(() =>
            reader.CountByRepositoryAsync(queuedId, default));
        Assert.Equal("PlanRefEvidenceUnavailable", ex.Code);
    }

    [Fact]
    public async Task Post_success_same_count_ref_mutation_fails_closed()
    {
        await using var db = fixture.NewContext();
        var source = Guid.NewGuid();
        var snapshot = await CompletedExternalizedAsync(db, TimeProvider.System, source,
            [Ref("repo-A", "refs/heads/main", "sha1")]);
        var reader = new PostgresPlanRefEvidenceReader(db);
        Assert.Equal(1, (await reader.CountByRepositoryAsync(snapshot, default))["repo-A"]);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE discovery_ref_observations
            SET payload_json = {"{\"ObjectId\":\"changed-after-success\"}"}::jsonb
            WHERE job_id = {snapshot}
            """);
        var fail = await Assert.ThrowsAsync<PlanInputException>(() =>
            reader.CountByRepositoryAsync(snapshot, default));
        Assert.Equal("PlanRefEvidenceMismatch", fail.Code);
    }

    [Fact]
    public async Task Legacy_inline_snapshot_counts_are_read_from_JSONB_without_materializing_refs()
    {
        await using var db = fixture.NewContext();
        var id = Guid.NewGuid();
        await SaveSnapshotAsync(db, id, Guid.NewGuid(),
            [Ref("repo-A", "refs/heads/main", "s1"),
             Ref("repo-A", "refs/tags/v1", "s2"),
             Ref("repo-B", "refs/heads/main", "s3")]);
        var rows = await new PostgresPlanRefEvidenceReader(db)
            .CountByRepositoryAsync(id, default);
        Assert.Equal(2, rows["repo-A"]);
        Assert.Equal(1, rows["repo-B"]);
        Assert.Equal(2, rows.Count);
    }

    private static RepositoryRef Ref(string repo, string name, string sha) =>
        new(repo, name, sha);

    private static async Task<Guid> CompletedExternalizedAsync(
        AppDbContext db, TimeProvider clock, Guid source, RepositoryRef[] refs)
    {
        // Exercise the fenced worker state deterministically: other tests
        // share a database queue and may leave unrelated jobs unprocessed.
        var jobs = new PostgresMigrationJobRepository(db, clock);
        var id = Guid.NewGuid();
        var token = Guid.NewGuid();
        var now = clock.GetUtcNow();
        db.MigrationJobs.Add(new MigrationJobRecord {
            Id = id, SourceConnectionId = source, SnapshotId = Guid.Empty,
            Kind = MigrationJobKinds.DiscoverSource, State = MigrationJobStates.Running,
            Attempts = 1, EnqueuedBy = Guid.NewGuid(),
            IdempotencyKey = "f017-ref-test:" + id,
            LeaseToken = token, LeaseUntilUtc = now.AddMinutes(3),
            AvailableAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now
        });
        await db.SaveChangesAsync();
        var lease = new MigrationJobLease(id, Guid.Empty, source,
            MigrationJobKinds.DiscoverSource, token, 1);
        await SaveSnapshotAsync(db, id, source, refs,
            externalized: true, expected: refs.Length);
        Assert.True(await jobs.CheckpointSnapshotAsync(lease, id, default));
        lease = lease with { SnapshotId = id };
        var pages = new PostgresDiscoveryPageCheckpointStore(db, clock);
        foreach (var group in refs.GroupBy(r => r.RepositoryId))
        {
            var payload = JsonSerializer.Serialize(new {
                value = group.Select(r => new { name = r.Name, objectId = r.ObjectId })
            });
            await pages.SaveAsync(new(lease.Id, lease.LeaseToken, lease.Attempt),
                new DiscoveryPageCheckpoint(job.Id, "refs:" + group.Key, 0,
                    new string('a', 64), payload, null, clock.GetUtcNow()), default);
        }
        var manifests = new PostgresDiscoveryRefManifestStore(db, clock);
        await manifests.PrepareAsync(lease, refs.Length, default);
        var staging = new StagingSyncService(new PostgresDiscoveryRepository(db),
            new PostgresStagingRepository(db, clock, manifests));
        var result = await staging.SyncUsingRefManifestAsync(lease, default);
        Assert.True(await jobs.CompleteAsync(lease, result, default));
        return id;
    }

    private static async Task SaveSnapshotAsync(
        AppDbContext db, Guid id, Guid source, RepositoryRef[] refs,
        bool externalized = false, int expected = 0)
    {
        var now = DateTimeOffset.UtcNow;
        await new PostgresDiscoveryRepository(db).SaveAsync(new DiscoveryInventory {
            Id = id, ConnectionId = source, SourceProvider = "azure-devops-services",
            Organization = "az-org", CollectedAtUtc = now,
            Snapshot = new DiscoverySnapshot {
                SnapshotId = id, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = now,
                Projects = [new SourceProject("project", "Project", [
                    new SourceRepository("repo-A", "project", "Repo A", VersionControlKind.Git),
                    new SourceRepository("repo-B", "project", "Repo B", VersionControlKind.Git)])],
                Identities = [], AccessGrants = [], PermissionsComplete = false
            },
            Refs = externalized ? [] : refs,
            RefsExternalized = externalized, RefCount = externalized ? expected : 0,
            Issues = [], Completeness = DiscoveryCompleteness.Partial,
            IdentitiesComplete = false, PermissionsComplete = false
        }, default);
    }
}
