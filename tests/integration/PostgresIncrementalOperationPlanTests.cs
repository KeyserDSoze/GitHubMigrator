using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

public sealed class PostgresIncrementalOperationPlanTests(PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>
{
    [Fact]
    public async Task Six_hundred_refs_yield_immutable_bounded_dependency_graph_and_idempotent_manifest()
    {
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var seed = await SeedAsync(db, clock, 601);
        var service = NewService(db, clock);
        var plan = await service.CreateAsync(seed.Run.Id, seed.OperatorId,
            seed.Preflight.EvidenceSha256, default);

        Assert.Equal(2, plan.RepositoryCount);
        Assert.Equal(12, plan.StepCount);
        Assert.Equal(601, plan.ChangedRefCount);
        Assert.Equal(0, plan.SensitiveChangeCount);
        Assert.Equal(64, plan.ManifestSha256.Length);
        Assert.True(plan.IsCurrent);
        Assert.False(plan.ReadyForExecution);
        Assert.Contains("TargetAvailabilityUnverified", plan.Blockers);
        Assert.Contains("DestinationWritePermissionUnverified", plan.Blockers);
        Assert.Contains("ExecutionAdapterNotImplemented", plan.Blockers);
        Assert.All(plan.Steps, s => Assert.Equal("Blocked", s.State));
        Assert.Equal(6, plan.Steps.Count(x => x.SourceRepositoryId == "repo-1"));
        Assert.Equal(6, plan.Steps.Count(x => x.SourceRepositoryId == "repo-2"));
        Assert.Equal(601, plan.Steps[0].ChangedRefs);
        Assert.Equal(0, plan.Steps[6].ChangedRefs);
        Assert.Null(plan.Steps[0].DependsOnOrdinal);
        Assert.Equal(1, plan.Steps[1].DependsOnOrdinal);
        Assert.Null(plan.Steps[6].DependsOnOrdinal);
        Assert.Equal(7, plan.Steps[7].DependsOnOrdinal);

        var replay = await service.CreateAsync(seed.Run.Id, Guid.NewGuid(),
            seed.Preflight.EvidenceSha256, default);
        Assert.Equal(plan.Id, replay.Id);
        Assert.Equal(plan.ManifestSha256, replay.ManifestSha256);
        Assert.Equal(plan.Steps.ToArray(), replay.Steps.ToArray());
        Assert.Single(await service.ListAsync(seed.Run.Id, default));
        Assert.Equal(12, await db.IncrementalOperationSteps.AsNoTracking()
            .CountAsync(x => x.PlanId == plan.Id));
        Assert.Equal(1, await db.IncrementalOperationPlans.AsNoTracking()
            .CountAsync(x => x.PreparationId == seed.Run.Id));
        Assert.Empty(await service.EventsAsync(seed.Run.Id, plan.Id, default));
    }

    [Fact]
    public async Task All_execution_authorization_requests_are_denied_and_independently_audited()
    {
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var seed = await SeedAsync(db, clock);
        var service = NewService(db, clock);
        var plan = await service.CreateAsync(seed.Run.Id, seed.OperatorId,
            seed.Preflight.EvidenceSha256, default);

        Assert.Equal("ExecutionReviewerMustBeIndependent",
            (await Assert.ThrowsAsync<IncrementalOperationPlanException>(() =>
                service.RequestAuthorizationAsync(seed.Run.Id, plan.Id,
                    seed.OperatorId, plan.ManifestSha256, true, default))).Code);
        Assert.Equal("ExecutionReviewerMustBeIndependent",
            (await Assert.ThrowsAsync<IncrementalOperationPlanException>(() =>
                service.RequestAuthorizationAsync(seed.Run.Id, plan.Id,
                    seed.ReviewerId, plan.ManifestSha256, true, default))).Code);
        Assert.Equal("ExplicitRiskAcknowledgmentRequired",
            (await Assert.ThrowsAsync<IncrementalOperationPlanException>(() =>
                service.RequestAuthorizationAsync(seed.Run.Id, plan.Id,
                    Guid.NewGuid(), plan.ManifestSha256, false, default))).Code);
        Assert.Equal("OperationManifestDigestMismatch",
            (await Assert.ThrowsAsync<IncrementalOperationPlanException>(() =>
                service.RequestAuthorizationAsync(seed.Run.Id, plan.Id,
                    Guid.NewGuid(), new string('a', 64), true, default))).Code);

        var actor = Guid.NewGuid();
        var denied = await service.RequestAuthorizationAsync(
            seed.Run.Id, plan.Id, actor, plan.ManifestSha256, true, default);
        Assert.Equal("Denied", denied.Decision);
        Assert.Equal("DestinationWritePermissionUnverified", denied.ReasonCode);
        Assert.Equal(actor, denied.ActorId);
        Assert.True(denied.Id > 0);
        var second = await service.RequestAuthorizationAsync(
            seed.Run.Id, plan.Id, Guid.NewGuid(), plan.ManifestSha256, true, default);
        Assert.NotEqual(denied.Id, second.Id);
        Assert.Equal(2, (await service.EventsAsync(seed.Run.Id, plan.Id, default)).Count);
        Assert.DoesNotContain(await db.IncrementalExecutionAuthorizationAttempts
            .AsNoTracking().Where(x => x.PlanId == plan.Id).ToArrayAsync(),
            x => x.Decision != "Denied");
        Assert.False((await service.GetAsync(seed.Run.Id, plan.Id, default))!.ReadyForExecution);
    }

    [Fact]
    public async Task New_live_proof_creates_an_append_only_version_and_old_plan_becomes_stale()
    {
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var seed = await SeedAsync(db, clock);
        var service = NewService(db, clock);
        var first = await service.CreateAsync(seed.Run.Id, seed.OperatorId,
            seed.Preflight.EvidenceSha256, default);
        clock.Advance(TimeSpan.FromMinutes(1));
        var rechecked = await new PostgresIncrementalTargetPreflightRepository(db, clock)
            .SaveAsync(Evidence(seed, clock), default);
        Assert.NotEqual(seed.Preflight.EvidenceSha256, rechecked.EvidenceSha256);
        var stale = await service.GetAsync(seed.Run.Id, first.Id, default);
        Assert.NotNull(stale);
        Assert.False(stale.IsCurrent);
        Assert.Contains("OperationPlanStale", stale.Blockers);
        Assert.Equal("OperationPreflightDigestMismatch",
            (await Assert.ThrowsAsync<IncrementalOperationPlanException>(() =>
                service.CreateAsync(seed.Run.Id, Guid.NewGuid(),
                    seed.Preflight.EvidenceSha256, default))).Code);
        var next = await service.CreateAsync(seed.Run.Id, Guid.NewGuid(),
            rechecked.EvidenceSha256, default);
        Assert.NotEqual(first.Id, next.Id);
        Assert.NotEqual(first.ManifestSha256, next.ManifestSha256);
        Assert.True(next.IsCurrent);
        Assert.Equal(2, (await service.ListAsync(seed.Run.Id, default)).Count);
        Assert.Equal("OperationPlanNotCurrent",
            (await service.RequestAuthorizationAsync(seed.Run.Id, first.Id,
                Guid.NewGuid(), first.ManifestSha256, true, default)).ReasonCode);
    }

    [Fact]
    public async Task Same_count_journal_tampering_cannot_produce_an_immutable_manifest()
    {
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var seed = await SeedAsync(db, clock);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE incremental_preparation_items
            SET current_fingerprint = {new string('a', 64)}
            WHERE preparation_id = {seed.Run.Id}
              AND asset_type = {"ref"}
            """);
        var service = NewService(db, clock);
        var ex = await Assert.ThrowsAsync<IncrementalOperationPlanException>(() =>
            service.CreateAsync(seed.Run.Id, Guid.NewGuid(),
                seed.Preflight.EvidenceSha256, default));
        Assert.Equal("OperationJournalMismatch", ex.Code);
        Assert.Empty(await service.ListAsync(seed.Run.Id, default));
        Assert.Equal(0, await db.IncrementalOperationPlans.AsNoTracking()
            .CountAsync(x => x.PreparationId == seed.Run.Id));
    }

    [Fact]
    public async Task Mutated_operation_graph_is_detected_by_digest_and_dependency_validation()
    {
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var seed = await SeedAsync(db, clock);
        var service = NewService(db, clock);
        var plan = await service.CreateAsync(seed.Run.Id, Guid.NewGuid(),
            seed.Preflight.EvidenceSha256, default);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE incremental_operation_plan_steps
            SET changed_refs = changed_refs + 1
            WHERE plan_id = {plan.Id} AND ordinal = 1
            """);
        Assert.Equal("OperationPlanIntegrityFailed",
            (await Assert.ThrowsAsync<IncrementalOperationPlanException>(() =>
                service.GetAsync(seed.Run.Id, plan.Id, default))).Code);
    }

    [Fact]
    public async Task Rejected_or_expired_preflight_never_allows_creating_a_plan()
    {
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var seed = await SeedAsync(db, clock);
        var service = NewService(db, clock);
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal("OperationPlanProofNotCurrent",
            (await Assert.ThrowsAsync<IncrementalOperationPlanException>(() =>
                service.CreateAsync(seed.Run.Id, Guid.NewGuid(),
                    seed.Preflight.EvidenceSha256, default))).Code);
        Assert.Empty(await service.ListAsync(seed.Run.Id, default));
    }

    [Fact]
    public async Task Two_concurrent_replicas_converge_on_one_manifest_without_duplicate_steps()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        Seed seed;
        await using (var setup = fixture.NewContext())
            seed = await SeedAsync(setup, clock);
        await using var a = fixture.NewContext();
        await using var b = fixture.NewContext();
        var one = NewService(a, clock);
        var two = NewService(b, clock);
        var results = await Task.WhenAll(
            one.CreateAsync(seed.Run.Id, Guid.NewGuid(), seed.Preflight.EvidenceSha256, default),
            two.CreateAsync(seed.Run.Id, Guid.NewGuid(), seed.Preflight.EvidenceSha256, default));
        Assert.Equal(results[0].Id, results[1].Id);
        Assert.Equal(results[0].ManifestSha256, results[1].ManifestSha256);
        Assert.Equal(12, await a.IncrementalOperationSteps.AsNoTracking()
            .CountAsync(x => x.PlanId == results[0].Id));
    }

    private sealed record Seed(IncrementalPreparationView Run,
        IncrementalReviewView Review, IncrementalTargetPreflightView Preflight,
        Guid OperatorId, Guid ReviewerId);

    private static IncrementalOperationPlanService NewService(
        AppDbContext db, TimeProvider clock) =>
        new(new PostgresIncrementalOperationPlanRepository(db, clock));

    private static IncrementalTargetPreflightEvidence Evidence(Seed seed, TimeProvider clock) =>
        new(seed.Run.Id, seed.Run.SnapshotId, seed.Run.SourceConnectionId,
            seed.Run.DestinationConnectionId, Guid.NewGuid(), "gh-org",
            seed.Review.DryRunSha256, seed.Run.CreatedAtUtc,
            seed.Run.CreatedAtUtc,
            DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds()),
            "Active", "Owner", "AllowedForMembers",
            [
                new("repo-1", "migration-one", "GitMirror", "NotVisibleOrMissing"),
                new("repo-2", "migration-two", "GitMirror", "NotVisibleOrMissing")
            ]);

    private static async Task<Seed> SeedAsync(
        AppDbContext db, ManualClock clock, int refs = 1)
    {
        var source = Guid.NewGuid();
        var destination = Guid.NewGuid();
        var author = Guid.NewGuid();
        var reviewer = Guid.NewGuid();
        var now = DateTimeOffset.FromUnixTimeMilliseconds(
            clock.GetUtcNow().ToUnixTimeMilliseconds());
        var connections = new PostgresConnectionRepository(db);
        await connections.SaveAsync(new(source, "Source", "azure-devops-services",
            ConnectionDirection.Source, "az-org", "source-secret", now, now,
            "Verified", "source-user", "read_only"), default);
        await connections.SaveAsync(new(destination, "Destination", "github-enterprise-cloud",
            ConnectionDirection.Destination, "gh-org", "dest-secret", now, now,
            "Verified", "target-user", "organization_write_unverified"), default);
        var id = Guid.NewGuid();
        var snapshot = new DiscoveryInventory {
            Id = id, ConnectionId = source,
            SourceProvider = "azure-devops-services", Organization = "az-org",
            CollectedAtUtc = now.AddMinutes(-3),
            Snapshot = new DiscoverySnapshot {
                SnapshotId = id, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = now.AddMinutes(-3),
                Projects = [new SourceProject("project", "Project", [
                    new SourceRepository("repo-1", "project", "One", VersionControlKind.Git),
                    new SourceRepository("repo-2", "project", "Two", VersionControlKind.Git)])],
                Identities = [], AccessGrants = []
            },
            Refs = Enumerable.Range(0, refs).Select(i =>
                new RepositoryRef("repo-1", $"refs/heads/b-{i:0000}", $"sha-{i:0000}"))
                .ToArray(),
            Issues = [], Completeness = DiscoveryCompleteness.Partial,
            IdentitiesComplete = false, PermissionsComplete = false
        };
        await new PostgresDiscoveryRepository(db).SaveAsync(snapshot, default);
        var normalized = StagingAssetNormalizer.Normalize(snapshot);
        await new PostgresStagingRepository(db, clock)
            .ApplyAsync(snapshot, normalized.Assets, normalized.CompleteTypes, default);
        var queue = new PostgresIncrementalPreparationRepository(db, clock);
        var queued = await queue.EnqueueAsync(id, source, destination, author, default);
        var lease = await queue.ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(5),
            default, source);
        Assert.NotNull(lease);
        Assert.True(await queue.PrepareAsync(lease, default));
        var run = (await queue.GetAsync(queued.Id, default))!;
        var reviews = new PostgresIncrementalReviewRepository(db, clock);
        var dryrun = await reviews.DryRunAsync(run.Id, author, default);
        var approved = await reviews.DecideAsync(run.Id, reviewer,
            "ApproveForPlanning", dryrun.DryRunSha256, false, null, default);
        var evidence = new IncrementalTargetPreflightEvidence(
            run.Id, run.SnapshotId, run.SourceConnectionId,
            run.DestinationConnectionId, Guid.NewGuid(), "gh-org",
            approved.DryRunSha256, now, now,
            DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds()),
            "Active", "Owner", "AllowedForMembers",
            [
                new("repo-1", "migration-one", "GitMirror", "NotVisibleOrMissing"),
                new("repo-2", "migration-two", "GitMirror", "NotVisibleOrMissing")
            ]);
        var preflight = await new PostgresIncrementalTargetPreflightRepository(db, clock)
            .SaveAsync(evidence, default);
        Assert.True(preflight.IsCurrent);
        return new(run, approved, preflight, author, reviewer);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan time) => now = now.Add(time);
    }
}
