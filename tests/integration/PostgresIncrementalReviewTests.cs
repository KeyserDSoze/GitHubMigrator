using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Real PostgreSQL 17, with no provider API calls. The review state is
// intentionally NOT a destination-execution or security-grant capability.
public sealed class PostgresIncrementalReviewTests(PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>
{
    [Fact]
    public async Task More_than_500_items_have_stable_dryrun_evidence_and_two_person_review()
    {
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var author = Guid.NewGuid();
        var reviewer = Guid.NewGuid();
        var (run, _, _) = await SeedAsync(db, clock,
            Enumerable.Range(0, 521).Select(i =>
                new RepositoryRef("repo-1", $"refs/heads/{i:0000}", $"sha{i:0000}")).ToArray(),
            author, security: true);
        var repo = new PostgresIncrementalReviewRepository(db, clock);
        var service = new IncrementalReviewService(repo);

        var dryRun = await service.DryRunAsync(run.Id, author, default);
        Assert.Equal(IncrementalReviewStates.DryRunReady, dryRun.State);
        Assert.Equal(525, dryRun.ItemCount);
        Assert.Equal(2, dryRun.SensitiveItemCount);
        Assert.Equal(0, dryRun.RemovalItemCount);
        Assert.True(dryRun.IsCurrent);
        Assert.Equal(run.EvidenceSha256, dryRun.EvidenceSha256);
        Assert.Equal(64, dryRun.DryRunSha256.Length);
        Assert.False(dryRun.SensitiveAcknowledged);
        Assert.Equal(dryRun.DryRunSha256,
            (await service.DryRunAsync(run.Id, reviewer, default)).DryRunSha256);
        Assert.Single(await service.EventsAsync(run.Id, default));

        var self = await Assert.ThrowsAsync<IncrementalReviewException>(() =>
            service.DecideAsync(run.Id, author, "ApproveForPlanning",
                dryRun.DryRunSha256, true, null, default));
        Assert.Equal("ReviewerMustBeIndependent", self.Code);

        var unacknowledged = await Assert.ThrowsAsync<IncrementalReviewException>(() =>
            service.DecideAsync(run.Id, reviewer, "ApproveForPlanning",
                dryRun.DryRunSha256, false, null, default));
        Assert.Equal("SensitiveReviewAcknowledgmentRequired", unacknowledged.Code);

        var mismatched = await Assert.ThrowsAsync<IncrementalReviewException>(() =>
            service.DecideAsync(run.Id, reviewer, "ApproveForPlanning",
                new string('a', 64), true, null, default));
        Assert.Equal("ReviewDigestMismatch", mismatched.Code);

        var approved = await service.DecideAsync(run.Id, reviewer,
            "ApproveForPlanning", dryRun.DryRunSha256, true, null, default);
        Assert.Equal("ApprovedForPlanning", approved.State);
        Assert.Equal(reviewer, approved.ReviewerId);
        Assert.True(approved.SensitiveAcknowledged);
        Assert.True(approved.IsCurrent);
        var replay = await service.DecideAsync(run.Id, reviewer,
            "ApproveForPlanning", dryRun.DryRunSha256, true, null, default);
        Assert.Equal(approved, replay);
        Assert.Equal(["DryRunReady", "ApprovedForPlanning"],
            (await service.EventsAsync(run.Id, default))
                .Select(x => x.EventType).ToArray());
        Assert.Equal("ReviewAlreadyDecided",
            (await Assert.ThrowsAsync<IncrementalReviewException>(() =>
                service.DryRunAsync(run.Id, reviewer, default))).Code);
        Assert.Equal("ReviewDecisionConflict",
            (await Assert.ThrowsAsync<IncrementalReviewException>(() =>
                service.DecideAsync(run.Id, reviewer, "Reject",
                    dryRun.DryRunSha256, false, "ChangedMyMind", default))).Code);

        var revoked = await service.RevokeAsync(run.Id, reviewer,
            dryRun.DryRunSha256, "ReassessmentRequired", default);
        Assert.Equal("Revoked", revoked.State);
        Assert.Equal("ReassessmentRequired", revoked.ReasonCode);
        Assert.Equal(revoked, await service.RevokeAsync(run.Id, reviewer,
            dryRun.DryRunSha256, "ReassessmentRequired", default));
        Assert.Equal(["DryRunReady", "ApprovedForPlanning", "Revoked"],
            (await service.EventsAsync(run.Id, default))
                .Select(x => x.EventType).ToArray());
        Assert.Equal(525, await db.IncrementalPreparationItems.AsNoTracking()
            .CountAsync(x => x.PreparationId == run.Id));
        Assert.All(await db.IncrementalPreparationItems.AsNoTracking()
            .Where(x => x.PreparationId == run.Id).ToArrayAsync(),
            x => Assert.Equal("NeedsReview", x.ReviewState));
    }

    [Fact]
    public async Task Rejected_plan_is_terminal_and_same_reviewer_retry_is_idempotent()
    {
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var author = Guid.NewGuid(); var reviewer = Guid.NewGuid();
        var (run, _, _) = await SeedAsync(db, clock, [
            new("repo-1", "refs/heads/main", "abc") ], author);
        var service = new IncrementalReviewService(
            new PostgresIncrementalReviewRepository(db, clock));
        var proof = await service.DryRunAsync(run.Id, author, default);
        Assert.Equal("InvalidReviewDecision", (await Assert.ThrowsAsync<IncrementalReviewException>(
            () => service.DecideAsync(run.Id, reviewer, "Reject",
                proof.DryRunSha256, false, "free-form note with whitespace", default))).Code);
        var rejected = await service.DecideAsync(run.Id, reviewer, "Reject",
            proof.DryRunSha256, false, "SourceMappingUnsafe", default);
        Assert.Equal("Rejected", rejected.State);
        Assert.Equal("SourceMappingUnsafe", rejected.ReasonCode);
        Assert.Equal(rejected, await service.DecideAsync(run.Id, reviewer, "Reject",
            proof.DryRunSha256, false, "SourceMappingUnsafe", default));
        Assert.Equal("ReviewDecisionConflict",
            (await Assert.ThrowsAsync<IncrementalReviewException>(() =>
                service.DecideAsync(run.Id, Guid.NewGuid(), "Reject",
                    proof.DryRunSha256, false, "SourceMappingUnsafe", default))).Code);
        Assert.Equal("ReviewDecisionConflict",
            (await Assert.ThrowsAsync<IncrementalReviewException>(() =>
                service.RevokeAsync(run.Id, reviewer, proof.DryRunSha256,
                    "NotApproved", default))).Code);
        Assert.Equal(2, (await service.EventsAsync(run.Id, default)).Count);
    }

    [Fact]
    public async Task Same_count_journal_tampering_fails_closed_before_dryrun_write()
    {
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var author = Guid.NewGuid();
        var (run, _, _) = await SeedAsync(db, clock,
            [new("repo-1", "refs/heads/main", "abc")], author);
        var changes = await db.StagingAssetChanges.AsNoTracking()
            .SingleAsync(x => x.SnapshotId == run.SnapshotId && x.AssetType == "ref");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE staging_asset_changes
            SET current_fingerprint = {new string('a', 64)}
            WHERE snapshot_id = {run.SnapshotId} AND asset_type = {"ref"}
                AND source_id = {changes.SourceId}
            """);
        var service = new IncrementalReviewService(
            new PostgresIncrementalReviewRepository(db, clock));
        var error = await Assert.ThrowsAsync<IncrementalReviewException>(() =>
            service.DryRunAsync(run.Id, author, default));
        Assert.Equal("ReviewEvidenceMismatch", error.Code);
        Assert.Null(await service.GetAsync(run.Id, default));
        Assert.Empty(await service.EventsAsync(run.Id, default));
    }

    [Fact]
    public async Task Same_count_prepared_item_tampering_blocks_approval_after_valid_dryrun()
    {
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var author = Guid.NewGuid(); var reviewer = Guid.NewGuid();
        var (run, _, _) = await SeedAsync(db, clock,
            [new("repo-1", "refs/heads/main", "abc")], author);
        var service = new IncrementalReviewService(
            new PostgresIncrementalReviewRepository(db, clock));
        var proof = await service.DryRunAsync(run.Id, author, default);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE incremental_preparation_items
            SET revision = revision + 1
            WHERE preparation_id = {run.Id} AND asset_type = {"ref"}
            """);
        var error = await Assert.ThrowsAsync<IncrementalReviewException>(() =>
            service.DecideAsync(run.Id, reviewer, "ApproveForPlanning",
                proof.DryRunSha256, false, null, default));
        Assert.Equal("ReviewEvidenceMismatch", error.Code);
        Assert.Equal("DryRunReady", (await service.GetAsync(run.Id, default))!.State);
        Assert.Single(await service.EventsAsync(run.Id, default));
    }

    [Fact]
    public async Task Newer_scan_invalidates_dryrun_and_historical_approval_is_not_current()
    {
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var author = Guid.NewGuid(); var reviewer = Guid.NewGuid();
        var (run, source, _) = await SeedAsync(db, clock,
            [new("repo-1", "refs/heads/main", "abc")], author);
        var service = new IncrementalReviewService(
            new PostgresIncrementalReviewRepository(db, clock));
        var proof = await service.DryRunAsync(run.Id, author, default);
        await StageNewerAsync(db, clock, source);
        Assert.False((await service.GetAsync(run.Id, default))!.IsCurrent);
        var error = await Assert.ThrowsAsync<IncrementalReviewException>(() =>
            service.DecideAsync(run.Id, reviewer, "ApproveForPlanning",
                proof.DryRunSha256, false, null, default));
        Assert.Equal("IncrementalSnapshotSuperseded", error.Code);

        var (other, otherSource, _) = await SeedAsync(db, clock,
            [new("repo-1", "refs/heads/develop", "def")], Guid.NewGuid());
        var approvedProof = await service.DryRunAsync(other.Id, author, default);
        Assert.Equal("ApprovedForPlanning", (await service.DecideAsync(
            other.Id, reviewer, "ApproveForPlanning",
            approvedProof.DryRunSha256, false, null, default)).State);
        await StageNewerAsync(db, clock, otherSource);
        var historical = await service.GetAsync(other.Id, default);
        Assert.Equal("ApprovedForPlanning", historical!.State);
        Assert.False(historical.IsCurrent); // never usable as a current review
    }

    [Fact]
    public async Task Revalidating_destination_demands_a_fresh_dryrun_digest()
    {
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var author = Guid.NewGuid(); var reviewer = Guid.NewGuid();
        var (run, _, destinationId) = await SeedAsync(db, clock,
            [new("repo-1", "refs/heads/main", "abc")], author);
        var service = new IncrementalReviewService(
            new PostgresIncrementalReviewRepository(db, clock));
        var old = await service.DryRunAsync(run.Id, author, default);

        var destination = await db.Connections.AsNoTracking()
            .SingleAsync(x => x.Id == destinationId);
        var newerValidation = destination.ValidatedAtUtc!.Value.AddMinutes(2);
        await db.Connections.Where(x => x.Id == destinationId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.ValidatedAtUtc, newerValidation));
        var stale = await service.GetAsync(run.Id, default);
        Assert.False(stale!.IsCurrent);
        Assert.Equal("DryRunOutdated",
            (await Assert.ThrowsAsync<IncrementalReviewException>(() =>
                service.DecideAsync(run.Id, reviewer, "ApproveForPlanning",
                    old.DryRunSha256, false, null, default))).Code);

        var refreshed = await service.DryRunAsync(run.Id, author, default);
        Assert.NotEqual(old.DryRunSha256, refreshed.DryRunSha256);
        Assert.Equal(2, (await service.EventsAsync(run.Id, default)).Count);
        Assert.Equal("ApprovedForPlanning", (await service.DecideAsync(
            run.Id, reviewer, "ApproveForPlanning", refreshed.DryRunSha256,
            false, null, default)).State);
    }

    [Fact]
    public async Task Concurrent_distinct_decisions_commit_exactly_one_review_event()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        Guid id, reviewerA = Guid.NewGuid(), reviewerB = Guid.NewGuid();
        string hash;
        await using (var setup = fixture.NewContext())
        {
            var (run, _, _) = await SeedAsync(setup, clock,
                [new("repo-1", "refs/heads/main", "abc")], Guid.NewGuid());
            id = run.Id;
            hash = (await new IncrementalReviewService(
                new PostgresIncrementalReviewRepository(setup, clock))
                .DryRunAsync(id, Guid.NewGuid(), default)).DryRunSha256;
        }
        await using var first = fixture.NewContext();
        await using var second = fixture.NewContext();
        var one = new IncrementalReviewService(
            new PostgresIncrementalReviewRepository(first, clock));
        var two = new IncrementalReviewService(
            new PostgresIncrementalReviewRepository(second, clock));
        var results = await Task.WhenAll(
            Capture(() => one.DecideAsync(id, reviewerA, "ApproveForPlanning",
                hash, false, null, default)),
            Capture(() => two.DecideAsync(id, reviewerB, "Reject",
                hash, false, "ConflictingReview", default)));
        Assert.Single(results, x => x.Success);
        Assert.Single(results, x => !x.Success);
        Assert.Equal("ReviewDecisionConflict", results.Single(x => !x.Success).Error);
        Assert.Equal(2, (await one.EventsAsync(id, default)).Count);
    }

    private static async Task<(bool Success, string? Error)> Capture(
        Func<Task<IncrementalReviewView>> action)
    {
        try { await action(); return (true, null); }
        catch (IncrementalReviewException ex) { return (false, ex.Code); }
    }

    private static async Task<(
        IncrementalPreparationView Run, Guid SourceId, Guid DestinationId)> SeedAsync(
        AppDbContext db, ManualClock clock, IReadOnlyList<RepositoryRef> refs,
        Guid author, bool security = false)
    {
        var source = Guid.NewGuid(); var destination = Guid.NewGuid();
        var now = clock.GetUtcNow();
        var connections = new PostgresConnectionRepository(db);
        await connections.SaveAsync(new(source, "Source", "azure-devops-services",
            ConnectionDirection.Source, "az-org", "source-reference", now, now,
            "Verified", "source-user", "read_only"), default);
        await connections.SaveAsync(new(destination, "Destination", "github-enterprise-cloud",
            ConnectionDirection.Destination, "gh-org", "destination-reference", now, now,
            "Verified", "destination-user", "read_only"), default);
        var id = Guid.NewGuid();
        var snapshot = new DiscoveryInventory {
            Id = id, ConnectionId = source, SourceProvider = "azure-devops-services",
            Organization = "az-org", CollectedAtUtc = now.AddMinutes(-3),
            Snapshot = new DiscoverySnapshot {
                SnapshotId = id, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = now.AddMinutes(-3),
                Projects = [new SourceProject("project", "Project",
                    [new SourceRepository("repo-1", "project", "Repo", VersionControlKind.Git)])],
                Identities = [], AccessGrants = []
            },
            Refs = refs,
            Groups = security ? [new DiscoveryGroup("vssgp.group", "Operators", null)] : [],
            AclEvidence = security ? [new RepositoryAclEvidence("repo-1",
                "repoV2/project/repo-1", "vssgp.group", 2, 0, false, "repository")] : [],
            Issues = [], Completeness = DiscoveryCompleteness.Partial,
            IdentitiesComplete = false, PermissionsComplete = false
        };
        var normalized = StagingAssetNormalizer.Normalize(snapshot);
        await new PostgresStagingRepository(db, clock)
            .ApplyAsync(snapshot, normalized.Assets, normalized.CompleteTypes, default);
        var queue = new PostgresIncrementalPreparationRepository(db, clock, useDatabaseClock: false);
        var queued = await queue.EnqueueAsync(id, source, destination, author, default);
        var lease = await queue.ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(5), default, source);
        Assert.NotNull(lease);
        Assert.Equal(queued.Id, lease.Id);
        Assert.True(await queue.PrepareAsync(lease, default));
        var prepared = await queue.GetAsync(queued.Id, default);
        Assert.Equal("Prepared", prepared!.State);
        return (prepared, source, destination);
    }

    private static async Task StageNewerAsync(
        AppDbContext db, ManualClock clock, Guid source)
    {
        var now = clock.GetUtcNow();
        var id = Guid.NewGuid();
        var snapshot = new DiscoveryInventory {
            Id = id, ConnectionId = source, SourceProvider = "azure-devops-services",
            Organization = "az-org", CollectedAtUtc = now.AddMinutes(-1),
            Snapshot = new DiscoverySnapshot {
                SnapshotId = id, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = now.AddMinutes(-1),
                Projects = [new SourceProject("project", "Project",
                    [new SourceRepository("repo-1", "project", "Repo", VersionControlKind.Git)])],
                Identities = [], AccessGrants = []
            },
            Refs = [new RepositoryRef("repo-1", "refs/heads/main", "new")],
            Issues = [], Completeness = DiscoveryCompleteness.Partial,
            IdentitiesComplete = false, PermissionsComplete = false
        };
        var normalized = StagingAssetNormalizer.Normalize(snapshot);
        await new PostgresStagingRepository(db, clock)
            .ApplyAsync(snapshot, normalized.Assets, normalized.CompleteTypes, default);
    }

    private sealed class ManualClock(DateTimeOffset time) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => time;
        public void Advance(TimeSpan value) => time = time.Add(value);
    }
}
