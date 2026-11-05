using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// F-028: PostgreSQL-backed live preflight, provider READ stubs, and no
// destination writers. All source/destination/review fixtures use real DB.
public sealed class PostgresIncrementalTargetPreflightTests(PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>
{
    [Fact]
    public async Task Visible_collision_and_ambiguous_404_are_persisted_but_never_approved()
    {
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var seed = await SeedAsync(db, clock, approved: true);
        var probe = new FakeProbe((_, repository) =>
            Task.FromResult(repository == "existing-repo" ? "Exists" : "NotVisibleOrMissing"));
        var reader = new FakeAccessReader(new("Active", "Member", "RestrictedForMembers"));
        var service = Build(db, clock, probe, reader);
        var selected = new[] {
            new PlanSelectionInput("repo-1", "existing-repo", HistoryStrategy.GitMirror),
            new PlanSelectionInput("repo-2", "new-repo", HistoryStrategy.GitMirror)
        };

        var result = await service.CheckAsync(seed.Run.Id, Guid.NewGuid(),
            seed.Review!.DryRunSha256, selected, default);
        Assert.Equal("Blocked", result.State);
        Assert.True(result.IsCurrent);
        Assert.True(result.MembershipVerified);
        Assert.False(result.RepositoryCreatePermissionVerified);
        Assert.False(result.TargetAvailabilityVerified);
        Assert.False(result.ReadyForExecution);
        Assert.Equal(2, result.CheckedRepositories);
        Assert.Equal(1, result.VisibleCollisions);
        Assert.Equal(1, result.UncertainRepositories);
        Assert.Equal(64, result.EvidenceSha256.Length);
        Assert.Equal(["Exists", "NotVisibleOrMissing"],
            result.Repositories.Select(x => x.Status).ToArray());
        Assert.Equal(2, probe.Calls);
        Assert.Equal(1, reader.Calls);

        var replay = await service.CheckAsync(seed.Run.Id, Guid.NewGuid(),
            seed.Review.DryRunSha256, selected, default);
        Assert.Equal(result, replay);
        Assert.Single(await service.EventsAsync(seed.Run.Id, default));
        Assert.Equal(4, probe.Calls); // each new POST genuinely re-probes
        clock.Advance(TimeSpan.FromMinutes(1));
        var refreshed = await service.CheckAsync(seed.Run.Id, Guid.NewGuid(),
            seed.Review.DryRunSha256, selected, default);
        Assert.Equal(2, refreshed.Revision);
        Assert.NotEqual(result.EvidenceSha256, refreshed.EvidenceSha256);
        Assert.Equal(2, (await service.EventsAsync(seed.Run.Id, default)).Count);
    }

    [Fact]
    public async Task All_404_does_not_mean_available_even_with_active_membership()
    {
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var seed = await SeedAsync(db, clock, approved: true);
        var probe = new FakeProbe((_, _) => Task.FromResult("NotVisibleOrMissing"));
        var service = Build(db, clock, probe,
            new FakeAccessReader(new("Active", "Owner", "AllowedForMembers")));
        var result = await service.CheckAsync(seed.Run.Id, Guid.NewGuid(),
            seed.Review!.DryRunSha256,
            [new("repo-1", "apparently-free", HistoryStrategy.GitMirror)], default);
        Assert.Equal("Unverified", result.State);
        Assert.Equal(0, result.VisibleCollisions);
        Assert.Equal(1, result.UncertainRepositories);
        Assert.True(result.MembershipVerified);
        Assert.False(result.RepositoryCreatePermissionVerified);
        Assert.False(result.TargetAvailabilityVerified);
        Assert.False(result.ReadyForExecution);
    }

    [Fact]
    public async Task Unapproved_or_revoked_review_must_not_trigger_external_requests()
    {
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var seed = await SeedAsync(db, clock, approved: false);
        var probe = new FakeProbe((_, _) => Task.FromResult("Exists"));
        var reader = new FakeAccessReader(new("Active", "Member", "Unverified"));
        var service = Build(db, clock, probe, reader);
        var requested = new[] {
            new PlanSelectionInput("repo-1", "target-one", HistoryStrategy.GitMirror)
        };
        Assert.Equal("PlanningReviewNotCurrent",
            (await Assert.ThrowsAsync<IncrementalTargetPreflightException>(() =>
                service.CheckAsync(seed.Run.Id, Guid.NewGuid(),
                    seed.Review!.DryRunSha256, requested, default))).Code);
        Assert.Equal(0, probe.Calls);
        Assert.Equal(0, reader.Calls);
        var reviewer = Guid.NewGuid();
        var reviews = new IncrementalReviewService(
            new PostgresIncrementalReviewRepository(db, clock));
        var approved = await reviews.DecideAsync(seed.Run.Id, reviewer,
            "ApproveForPlanning", seed.Review!.DryRunSha256, false, null, default);
        Assert.Equal("ApprovedForPlanning", approved.State);
        await service.CheckAsync(seed.Run.Id, Guid.NewGuid(),
            approved.DryRunSha256, requested, default);
        Assert.Equal(1, probe.Calls);
        await reviews.RevokeAsync(seed.Run.Id, reviewer,
            approved.DryRunSha256, "ReviewWithdrawn", default);
        Assert.False((await service.GetAsync(seed.Run.Id, default))!.IsCurrent);
        Assert.Equal("PlanningReviewNotCurrent",
            (await Assert.ThrowsAsync<IncrementalTargetPreflightException>(() =>
                service.CheckAsync(seed.Run.Id, Guid.NewGuid(),
                    approved.DryRunSha256, requested, default))).Code);
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task Superseded_during_live_GET_cannot_commit_old_preflight()
    {
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var seed = await SeedAsync(db, clock, approved: true);
        var probe = new FakeProbe(async (_, _) =>
        {
            await using var another = fixture.NewContext();
            await StageNewerAsync(another, clock, seed.Run.SourceConnectionId);
            return "NotVisibleOrMissing";
        });
        var service = Build(db, clock, probe,
            new FakeAccessReader(new("Active", "Owner", "AllowedForMembers")));
        var error = await Assert.ThrowsAsync<IncrementalTargetPreflightException>(() =>
            service.CheckAsync(seed.Run.Id, Guid.NewGuid(),
                seed.Review!.DryRunSha256,
                [new("repo-1", "target-one", HistoryStrategy.GitMirror)], default));
        Assert.Equal("TargetPreflightContextChanged", error.Code);
        Assert.Null(await service.GetAsync(seed.Run.Id, default));
        Assert.Empty(await service.EventsAsync(seed.Run.Id, default));
    }

    [Fact]
    public async Task Changing_validation_after_GET_fails_closed_without_a_persisted_observation()
    {
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var seed = await SeedAsync(db, clock, approved: true);
        var probe = new FakeProbe(async (_, _) =>
        {
            await using var another = fixture.NewContext();
            await another.Connections.Where(x => x.Id == seed.Run.DestinationConnectionId)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.ValidatedAtUtc,
                    clock.GetUtcNow().AddSeconds(15)));
            return "NotVisibleOrMissing";
        });
        var service = Build(db, clock, probe,
            new FakeAccessReader(new("Active", "Owner", "AllowedForMembers")));
        var error = await Assert.ThrowsAsync<IncrementalTargetPreflightException>(() =>
            service.CheckAsync(seed.Run.Id, Guid.NewGuid(),
                seed.Review!.DryRunSha256,
                [new("repo-1", "target-one", HistoryStrategy.GitMirror)], default));
        Assert.Equal("TargetPreflightContextChanged", error.Code);
        Assert.Null(await service.GetAsync(seed.Run.Id, default));
    }

    [Fact]
    public async Task Equal_count_tampering_and_expired_evidence_are_detected()
    {
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var seed = await SeedAsync(db, clock, approved: true);
        var service = Build(db, clock,
            new FakeProbe((_, _) => Task.FromResult("NotVisibleOrMissing")),
            new FakeAccessReader(new("Active", "Member", "Unverified")));
        await service.CheckAsync(seed.Run.Id, Guid.NewGuid(),
            seed.Review!.DryRunSha256,
            [new("repo-1", "target-one", HistoryStrategy.GitMirror)], default);
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.False((await service.GetAsync(seed.Run.Id, default))!.IsCurrent);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE incremental_target_preflight_probes SET status = 'Exists'
            WHERE preparation_id = {seed.Run.Id}
            """);
        Assert.Equal("TargetPreflightIntegrityFailed",
            (await Assert.ThrowsAsync<IncrementalTargetPreflightException>(() =>
                service.GetAsync(seed.Run.Id, default))).Code);
    }

    [Fact]
    public async Task Duplicate_target_name_and_unknown_source_are_rejected_before_GET()
    {
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var seed = await SeedAsync(db, clock, approved: true);
        var probe = new FakeProbe((_, _) => Task.FromResult("Exists"));
        var service = Build(db, clock, probe,
            new FakeAccessReader(new("Active", "Member", "Unverified")));
        var duplicate = new[] {
            new PlanSelectionInput("repo-1", "Collision", HistoryStrategy.GitMirror),
            new PlanSelectionInput("repo-2", "collision", HistoryStrategy.GitMirror)
        };
        Assert.Equal("InvalidTargetRepositorySelection",
            (await Assert.ThrowsAsync<IncrementalTargetPreflightException>(() =>
                service.CheckAsync(seed.Run.Id, Guid.NewGuid(),
                    seed.Review!.DryRunSha256, duplicate, default))).Code);
        Assert.Equal(0, probe.Calls);
        await Assert.ThrowsAsync<PlanInputException>(() =>
            service.CheckAsync(seed.Run.Id, Guid.NewGuid(),
                seed.Review!.DryRunSha256,
                [new("unrecognized", "free", HistoryStrategy.GitMirror)], default));
        Assert.Equal(0, probe.Calls);
        Assert.Equal("InvalidTargetPreflightRequest",
            (await Assert.ThrowsAsync<IncrementalTargetPreflightException>(() =>
                service.CheckAsync(seed.Run.Id, Guid.NewGuid(),
                    seed.Review!.DryRunSha256,
                    Enumerable.Range(0, 11).Select(i =>
                        new PlanSelectionInput("repo-" + i,
                            "name-" + i, HistoryStrategy.GitMirror)).ToArray(), default))).Code);
    }

    private static IncrementalTargetPreflightService Build(
        AppDbContext db, TimeProvider clock, FakeProbe probe, FakeAccessReader reader)
    {
        var connections = new PostgresConnectionRepository(db);
        var secrets = new StubSecrets();
        var access = new GitHubDestinationAccessService(
            connections, secrets, reader, clock);
        var preview = new MigrationPlanReviewService(
            new PostgresDiscoveryRepository(db), connections,
            new PostgresIdentityMappingDraftRepository(db),
            null, new PostgresPlanRefEvidenceReader(db));
        return new(new PostgresIncrementalPreparationRepository(db, clock),
            new PostgresIncrementalReviewRepository(db, clock),
            preview, connections, secrets, access, probe,
            new PostgresIncrementalTargetPreflightRepository(db, clock), clock);
    }

    private static async Task<(IncrementalPreparationView Run,
        IncrementalReviewView? Review)> SeedAsync(
        AppDbContext db, ManualClock clock, bool approved)
    {
        var source = Guid.NewGuid(); var destination = Guid.NewGuid();
        var author = Guid.NewGuid(); var reviewer = Guid.NewGuid();
        var now = clock.GetUtcNow();
        var connections = new PostgresConnectionRepository(db);
        await connections.SaveAsync(new(source, "Source", "azure-devops-services",
            ConnectionDirection.Source, "az-org", "source-secret", now, now,
            "Verified", "source-user", "read_only"), default);
        await connections.SaveAsync(new(destination, "Destination", "github-enterprise-cloud",
            ConnectionDirection.Destination, "gh-org", "dest-secret", now, now,
            "Verified", "target-user", "organization_write_unverified"), default);
        var id = Guid.NewGuid();
        var snapshot = new DiscoveryInventory {
            Id = id, ConnectionId = source, SourceProvider = "azure-devops-services",
            Organization = "az-org", CollectedAtUtc = now.AddMinutes(-3),
            Snapshot = new DiscoverySnapshot {
                SnapshotId = id, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = now.AddMinutes(-3),
                Projects = [new SourceProject("project", "Project", [
                    new SourceRepository("repo-1", "project", "One", VersionControlKind.Git),
                    new SourceRepository("repo-2", "project", "Two", VersionControlKind.Git)])],
                Identities = [], AccessGrants = []
            },
            Refs = [new RepositoryRef("repo-1", "refs/heads/main", "abc")],
            Issues = [], Completeness = DiscoveryCompleteness.Partial,
            IdentitiesComplete = false, PermissionsComplete = false
        };
        await new PostgresDiscoveryRepository(db).SaveAsync(snapshot, default);
        var normalized = StagingAssetNormalizer.Normalize(snapshot);
        await new PostgresStagingRepository(db, clock)
            .ApplyAsync(snapshot, normalized.Assets, normalized.CompleteTypes, default);
        var jobs = new PostgresIncrementalPreparationRepository(db, clock);
        var queued = await jobs.EnqueueAsync(id, source, destination, author, default);
        var lease = await jobs.ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(5),
            default, source);
        Assert.NotNull(lease);
        Assert.True(await jobs.PrepareAsync(lease, default));
        var run = (await jobs.GetAsync(queued.Id, default))!;
        var reviewRepo = new PostgresIncrementalReviewRepository(db, clock);
        var review = await reviewRepo.DryRunAsync(run.Id, author, default);
        if (approved)
            review = await reviewRepo.DecideAsync(run.Id, reviewer,
                "ApproveForPlanning", review.DryRunSha256, false, null, default);
        return (run, review);
    }

    private static async Task StageNewerAsync(AppDbContext db, TimeProvider clock, Guid source)
    {
        var now = clock.GetUtcNow();
        var id = Guid.NewGuid();
        var snapshot = new DiscoveryInventory {
            Id = id, ConnectionId = source, SourceProvider = "azure-devops-services",
            Organization = "az-org", CollectedAtUtc = now.AddMinutes(-1),
            Snapshot = new DiscoverySnapshot {
                SnapshotId = id, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = now.AddMinutes(-1), Projects = [
                    new SourceProject("project", "Project",
                        [new SourceRepository("repo-1", "project", "One",
                            VersionControlKind.Git)])],
                Identities = [], AccessGrants = []
            },
            Refs = [], Issues = [], Completeness = DiscoveryCompleteness.Partial,
            IdentitiesComplete = false, PermissionsComplete = false
        };
        var normalized = StagingAssetNormalizer.Normalize(snapshot);
        await new PostgresStagingRepository(db, clock)
            .ApplyAsync(snapshot, normalized.Assets, normalized.CompleteTypes, default);
    }

    private sealed class StubSecrets : IConnectionSecretStore
    {
        public Task<string?> ReadAsync(string reference, CancellationToken ct) =>
            Task.FromResult<string?>(reference == "dest-secret" ? "fake-github-token" : null);
        public Task SaveAsync(string reference, string token, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task DeleteAsync(string reference, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeAccessReader(GitHubDestinationAccessEvidence reply)
        : IGitHubDestinationAccessReader
    {
        public int Calls { get; private set; }
        public Task<GitHubDestinationAccessEvidence> ReadAsync(
            string organization, string expectedLogin, string token, CancellationToken ct)
        {
            Assert.Equal("gh-org", organization);
            Assert.Equal("target-user", expectedLogin);
            Assert.Equal("fake-github-token", token);
            Calls++;
            return Task.FromResult(reply);
        }
    }

    private sealed class FakeProbe(
        Func<string, string, Task<string>> response) : IGitHubTargetRepositoryProbe
    {
        public int Calls { get; private set; }
        public async Task<string> ProbeAsync(
            string organization, string repository, string token, CancellationToken ct)
        {
            Assert.Equal("gh-org", organization);
            Assert.Equal("fake-github-token", token);
            Calls++;
            return await response(organization, repository);
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan interval) => now = now.Add(interval);
    }
}
