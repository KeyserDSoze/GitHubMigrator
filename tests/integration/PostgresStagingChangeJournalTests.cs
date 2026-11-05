using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Real PostgreSQL 17: atomic immutable source deltas, not instructions to
// change GitHub repositories, branches, identities or permissions.
public sealed class PostgresStagingChangeJournalTests(PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>
{
    [Fact]
    public async Task More_than_a_thousand_additions_are_immutable_keyset_paged_and_replay_safe()
    {
        var source = Guid.NewGuid();
        var scan = Snapshot(source, DateTimeOffset.UtcNow.AddMinutes(-8), "stable",
            refs: Enumerable.Range(0, 1_200).Select(i =>
                new RepositoryRef("repo-1", $"refs/heads/{i:0000}", "stable")).ToArray());
        await using var db = fixture.NewContext();
        var repo = new PostgresStagingRepository(db, TimeProvider.System);
        var inputs = StagingAssetNormalizer.Normalize(scan);
        var first = await repo.ApplyAsync(scan, inputs.Assets, inputs.CompleteTypes, default);
        Assert.Equal(1_202, first.Added);
        var summary = await repo.GetChangeSummaryAsync(scan.Id, default);
        Assert.NotNull(summary);
        Assert.Equal(source, summary.SourceConnectionId);
        Assert.Equal(1_202, summary.Added);
        Assert.Equal(1_202, summary.Total);
        Assert.Equal(0, summary.Changed + summary.Restored + summary.Missing);

        var firstPage = await repo.ListChangesAsync(scan.Id, "ref", null, 100, default);
        Assert.NotNull(firstPage);
        Assert.Equal(100, firstPage.Items.Count);
        Assert.NotNull(firstPage.NextAfter);
        Assert.All(firstPage.Items, x => {
            Assert.Equal("Added", x.Kind);
            Assert.Null(x.PreviousFingerprint);
            Assert.NotNull(x.CurrentFingerprint);
            Assert.Equal(1, x.Revision);
        });
        var nextPage = await repo.ListChangesAsync(
            scan.Id, "ref", firstPage.NextAfter, 100, default);
        Assert.NotNull(nextPage);
        Assert.Equal(100, nextPage.Items.Count);
        Assert.DoesNotContain(nextPage.Items, x =>
            firstPage.Items.Any(y => y.SourceId == x.SourceId));
        Assert.True(string.CompareOrdinal(firstPage.Items[^1].SourceId,
            nextPage.Items[0].SourceId) < 0);

        var replay = await repo.ApplyAsync(scan, inputs.Assets, inputs.CompleteTypes, default);
        Assert.Equal(first, replay);
        Assert.Equal(1_202, await db.StagingAssetChanges.AsNoTracking()
            .CountAsync(x => x.SnapshotId == scan.Id));
    }

    [Fact]
    public async Task Partial_scan_never_marks_missing_complete_scan_does_and_restoration_is_explicit()
    {
        var source = Guid.NewGuid();
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-9);
        var first = Snapshot(source, baseTime, "sha-main-original",
            refs: [new("repo-1", "refs/heads/main", "sha-main-original"),
                new("repo-1", "refs/heads/feature", "sha-feature")]);
        var second = Snapshot(source, baseTime.AddMinutes(1), "sha-main-changed",
            refs: [new("repo-1", "refs/heads/main", "sha-main-changed")]);
        var third = Snapshot(source, baseTime.AddMinutes(2), "sha-main-changed",
            completeness: DiscoveryCompleteness.Complete,
            refs: [new("repo-1", "refs/heads/main", "sha-main-changed")]);
        var fourth = Snapshot(source, baseTime.AddMinutes(3), "sha-main-changed",
            completeness: DiscoveryCompleteness.Complete,
            refs: [new("repo-1", "refs/heads/main", "sha-main-changed"),
                new("repo-1", "refs/heads/feature", "sha-feature")]);
        var fifth = Snapshot(source, baseTime.AddMinutes(4), "sha-main-changed",
            completeness: DiscoveryCompleteness.Complete,
            refs: [new("repo-1", "refs/heads/main", "sha-main-changed"),
                new("repo-1", "refs/heads/feature", "sha-feature")]);

        await using var db = fixture.NewContext();
        var repo = new PostgresStagingRepository(db, TimeProvider.System);
        async Task<StagingSyncResult> Apply(DiscoveryInventory snapshot)
        {
            var data = StagingAssetNormalizer.Normalize(snapshot);
            return await repo.ApplyAsync(snapshot, data.Assets, data.CompleteTypes, default);
        }

        Assert.Equal(4, (await Apply(first)).Added);
        Assert.Equal(new StagingChangeSummary(first.Id, source, 4, 0, 0, 0),
            await repo.GetChangeSummaryAsync(first.Id, default));

        var updated = await Apply(second);
        Assert.Equal(1, updated.Changed);
        Assert.Equal(0, updated.MarkedMissing);
        var partialDelta = await repo.GetChangeSummaryAsync(second.Id, default);
        Assert.Equal(new StagingChangeSummary(second.Id, source, 0, 1, 0, 0), partialDelta);
        var changedRef = await repo.ListChangesAsync(second.Id, "ref", null, 10, default);
        Assert.Equal("Changed", Assert.Single(changedRef!.Items).Kind);
        Assert.NotEqual(changedRef.Items[0].PreviousFingerprint,
            changedRef.Items[0].CurrentFingerprint);

        var complete = await Apply(third);
        Assert.Equal(1, complete.MarkedMissing);
        var missingDelta = await repo.GetChangeSummaryAsync(third.Id, default);
        Assert.Equal(new StagingChangeSummary(third.Id, source, 0, 0, 0, 1), missingDelta);
        var missingRef = Assert.Single((await repo.ListChangesAsync(third.Id,
            "ref", null, 10, default))!.Items);
        Assert.Equal("Missing", missingRef.Kind);
        Assert.NotNull(missingRef.PreviousFingerprint);
        Assert.Null(missingRef.CurrentFingerprint);
        Assert.Equal(2, missingRef.Revision);

        var restored = await Apply(fourth);
        Assert.Equal(1, restored.Changed); // F-013 counts a recovery as changed
        Assert.Equal(0, restored.Added);
        var recoveredDelta = await repo.GetChangeSummaryAsync(fourth.Id, default);
        Assert.Equal(new StagingChangeSummary(fourth.Id, source, 0, 0, 1, 0),
            recoveredDelta);
        var recoveredRef = Assert.Single((await repo.ListChangesAsync(fourth.Id,
            "ref", null, 10, default))!.Items);
        Assert.Equal("Restored", recoveredRef.Kind);
        Assert.Equal(recoveredRef.PreviousFingerprint, recoveredRef.CurrentFingerprint);
        Assert.Equal(3, recoveredRef.Revision);

        var noChanges = await Apply(fifth);
        Assert.Equal(0, noChanges.Added + noChanges.Changed + noChanges.MarkedMissing);
        Assert.Equal(0, (await repo.GetChangeSummaryAsync(fifth.Id, default))!.Total);
        Assert.Empty((await repo.ListChangesAsync(fifth.Id, "ref", null, 10, default))!.Items);
        // Historical third-scan deletion evidence survives later restoration.
        Assert.Equal("Missing", Assert.Single((await repo.ListChangesAsync(
            third.Id, "ref", null, 10, default))!.Items).Kind);
    }

    [Fact]
    public async Task A_failure_after_the_first_250_rows_rolls_back_both_catalog_and_journal()
    {
        var source = Guid.NewGuid();
        var scan = Snapshot(source, DateTimeOffset.UtcNow, "sha");
        var malformed = Enumerable.Range(0, 300).Select(i => new StagingAssetInput(
            "ref", $"ref-{i:0000}", null, new string('a', 64),
            i == 275 ? "{not-json" : "{}")).ToArray();
        await using (var db = fixture.NewContext())
        {
            var repo = new PostgresStagingRepository(db, TimeProvider.System);
            await Assert.ThrowsAnyAsync<Exception>(() => repo.ApplyAsync(
                scan, malformed, new HashSet<string>(), default));
        }
        await using (var db = fixture.NewContext())
        {
            Assert.Empty(await db.StagingAssetChanges.AsNoTracking()
                .Where(x => x.SnapshotId == scan.Id).ToArrayAsync());
            Assert.Empty(await db.StagingAssets.AsNoTracking()
                .Where(x => x.SourceConnectionId == source).ToArrayAsync());
            var repo = new PostgresStagingRepository(db, TimeProvider.System);
            Assert.Null(await repo.GetSyncAsync(scan.Id, default));
            var correct = StagingAssetNormalizer.Normalize(scan);
            var result = await repo.ApplyAsync(
                scan, correct.Assets, correct.CompleteTypes, default);
            Assert.Equal(3, result.Added);
            Assert.Equal(3, (await repo.GetChangeSummaryAsync(scan.Id, default))!.Added);
        }
    }

    [Fact]
    public async Task Pre_F025_sync_is_explicitly_unavailable_not_falsely_reported_as_empty()
    {
        var source = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var db = fixture.NewContext();
        db.StagingSnapshotSyncs.Add(new StagingSnapshotSyncRecord {
            SnapshotId = snapshotId, SourceConnectionId = source,
            SourceCollectedAtUtc = now, AppliedAtUtc = now,
            Observed = 3, Added = 3, Changed = 0, Unchanged = 0,
            MarkedMissing = 0, SourceInventoryComplete = false,
            JournalVersion = 0
        });
        await db.SaveChangesAsync();
        var repo = new PostgresStagingRepository(db, TimeProvider.System);
        await Assert.ThrowsAsync<StagingChangeJournalUnavailableException>(() =>
            repo.GetChangeSummaryAsync(snapshotId, default));
        await Assert.ThrowsAsync<StagingChangeJournalUnavailableException>(() =>
            repo.ListChangesAsync(snapshotId, "ref", null, 10, default));
    }

    [Fact]
    public async Task Deleted_change_evidence_is_detected_as_integrity_failure()
    {
        var source = Guid.NewGuid();
        var snapshot = Snapshot(source, DateTimeOffset.UtcNow, "sha");
        await using var db = fixture.NewContext();
        var repo = new PostgresStagingRepository(db, TimeProvider.System);
        var normalized = StagingAssetNormalizer.Normalize(snapshot);
        await repo.ApplyAsync(snapshot, normalized.Assets, normalized.CompleteTypes, default);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM staging_asset_changes
            WHERE snapshot_id = {snapshot.Id} AND asset_type = {"repository"}
            """);
        await Assert.ThrowsAsync<StagingChangeIntegrityException>(() =>
            repo.GetChangeSummaryAsync(snapshot.Id, default));
    }

    [Fact]
    public async Task Independent_sources_cannot_read_each_others_snapshot_events()
    {
        var a = Snapshot(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-1), "a");
        var b = Snapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, "b");
        await using var db = fixture.NewContext();
        var repo = new PostgresStagingRepository(db, TimeProvider.System);
        foreach (var snap in new[] { a, b })
        {
            var normalized = StagingAssetNormalizer.Normalize(snap);
            await repo.ApplyAsync(snap, normalized.Assets, normalized.CompleteTypes, default);
        }
        Assert.Equal(3, (await repo.GetChangeSummaryAsync(a.Id, default))!.Added);
        Assert.Equal(3, (await repo.GetChangeSummaryAsync(b.Id, default))!.Added);
        Assert.DoesNotContain(await db.StagingAssetChanges.AsNoTracking()
            .Where(x => x.SnapshotId == a.Id).ToArrayAsync(),
            x => x.SourceConnectionId == b.ConnectionId);
    }

    private static DiscoveryInventory Snapshot(
        Guid connectionId, DateTimeOffset at, string oid,
        DiscoveryCompleteness completeness = DiscoveryCompleteness.Partial,
        IReadOnlyList<RepositoryRef>? refs = null)
    {
        var id = Guid.NewGuid();
        return new DiscoveryInventory {
            Id = id, ConnectionId = connectionId,
            SourceProvider = "azure-devops-services", Organization = "az-org",
            CollectedAtUtc = at,
            Snapshot = new DiscoverySnapshot {
                SnapshotId = id, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = at,
                Projects = [new SourceProject("proj-1", "Project",
                    [new SourceRepository("repo-1", "proj-1", "Repo", VersionControlKind.Git)])],
                Identities = [], AccessGrants = []
            },
            Refs = refs ?? [new("repo-1", "refs/heads/main", oid)],
            Issues = [], Completeness = completeness,
            IdentitiesComplete = false, PermissionsComplete = false
        };
    }
}
