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
        // The negative assertion below proves this preflight never writes
        // a durable F-095 receipt, even on a database without other tests.
        await GitTransferWorkpackSchema.InitializeAsync(services);
        await GitSourceBundleSchema.InitializeAsync(services);
        await GitSourceMirrorLifecycleSchema.InitializeAsync(services);
        await GitIncrementalAncestryLedgerSchema.InitializeAsync(services);
        await GitIncrementalCheckpointWorkpackSchema.InitializeAsync(services);
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
        AppDbContext db, PostgresIncrementalOperationPlanTests.ManualClock clock,
        int oidLength = 40)
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
            new RepositoryRef("repo-1", "refs/heads/main", new string('a', oidLength)),
            new RepositoryRef("repo-1", "refs/tags/v1", new string('b', oidLength))
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

    private static PostgresGitIncrementalCheckpointAuditPreflight Preflight(
        AppDbContext db, TimeProvider clock, bool enabled = true) =>
        new(db, clock, new(enabled),
            new PostgresIncrementalOperationPlanRepository(db, clock),
            new PostgresGitHubSourceSyncCheckpointJournal(
                db, clock, new(true),
                new PostgresIncrementalOperationPlanRepository(db, clock),
                new PostgresStagingRepository(db, clock)),
            Ledger(db, clock));

    private static PostgresGitIncrementalCheckpointWorkpackJournal Workpacks(
        AppDbContext db, TimeProvider clock, bool enabled = true) =>
        new(db, clock, new(enabled), Preflight(db, clock), Ledger(db, clock),
            new PostgresIncrementalOperationPlanRepository(db, clock));

    // A genuine SECOND completed immutable F-014 source ref manifest, not
    // a relabelled baseline or a fingerprint-only F-025 staging delta.
    private static async Task<Guid> LaterF014SnapshotAsync(
        AppDbContext db, PostgresIncrementalOperationPlanTests.ManualClock clock,
        IncrementalOperationPlan plan, int oidLength)
    {
        var jobs = new PostgresMigrationJobRepository(db, clock);
        var job = await jobs.EnqueueDiscoveryAsync(
            plan.SourceConnectionId, Guid.NewGuid(), Guid.NewGuid(), default);
        var lease = (await jobs.ClaimForSourceAsync(
            plan.SourceConnectionId, Guid.NewGuid(),
            TimeSpan.FromMinutes(3), default))!;
        Assert.Equal(job.Id, lease.Id);
        var observedAt = clock.GetUtcNow().AddMinutes(2);
        var refs = new[] {
            new RepositoryRef("repo-1", "refs/heads/main",
                new string('c', oidLength)),
            new RepositoryRef("repo-1", "refs/tags/v1",
                new string('b', oidLength))
        };
        var inventory = new DiscoveryInventory
        {
            Id = job.Id, ConnectionId = plan.SourceConnectionId,
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
                        VersionControlKind.Git)])],
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
                new string('e', 64),
                JsonSerializer.Serialize(new {
                    value = refs.Select(x => new {
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
        return job.Id;
    }

    private static PostgresGitCheckpointTargetReReviewPrerequisites ReReview(
        AppDbContext db, TimeProvider clock, bool enabled = true) =>
        new(db, clock, new(enabled),
            new PostgresIncrementalOperationPlanRepository(db, clock),
            new PostgresGitHubSourceSyncCheckpointJournal(
                db, clock, new(true),
                new PostgresIncrementalOperationPlanRepository(db, clock),
                new PostgresStagingRepository(db, clock)),
            Ledger(db, clock));

    private static PostgresGitIncrementalCheckpointMirrorPreflight Physical(
        AppDbContext db, TimeProvider clock, bool enabled = true) =>
        new(db, clock, new(enabled),
            Preflight(db, clock), Ledger(db, clock),
            new PostgresIncrementalOperationPlanRepository(db, clock));

    [Theory]
    [InlineData(40)]
    [InlineData(64)]
    public async Task F097c4c1_real_second_snapshot_cannot_reuse_stale_F029_review(
        int oidLength)
    {
        var clock = new PostgresIncrementalOperationPlanTests.ManualClock(
            DateTimeOffset.UtcNow);
        IncrementalPreparationView run;
        IncrementalOperationPlan plan;
        GitHubSourceSyncCheckpointReceipt checkpoint;
        Guid actor;
        Guid laterId;

        await using (var db = fixture.NewContext())
        {
            var setup = await SetupAsync(db, clock, oidLength);
            run = setup.Preparation;
            plan = setup.Plan;
            checkpoint = setup.Checkpoint;
            actor = setup.ActorId;
            var initial = await Ledger(db, clock).CaptureAsync(
                run.Id, plan.Id, checkpoint.Id, "repo-1", actor, default);
            laterId = await LaterF014SnapshotAsync(
                db, clock, plan, oidLength);
            var later = await new PostgresGitHubSourceSyncCheckpointJournal(
                db, clock, new(true),
                new PostgresIncrementalOperationPlanRepository(db, clock),
                new PostgresStagingRepository(db, clock)).CaptureAsync(
                    run.Id, plan.Id, laterId, checkpoint.Id,
                    plan.ManifestSha256, actor, default);
            var verified = await Ledger(db, clock).CaptureAsync(
                run.Id, plan.Id, later.Id, "repo-1", actor, default);
            Assert.Equal(laterId, verified.SnapshotId);
            Assert.Equal(checkpoint.Id, verified.PreviousCheckpointId);
            Assert.Equal(initial.EvidenceSha256,
                verified.PreviousFullRefProofSha256);
            Assert.NotEqual(initial.SourceEvidenceSha256,
                verified.SourceEvidenceSha256);

            // The newer source epoch intentionally invalidates the
            // F-029 review/target preflight, so a legacy F-031 target
            // can no longer be sealed. Checkpoint-scoped review (#105)
            // is required, not a fabricated "current" flag.
            var stale = await new PostgresIncrementalOperationPlanRepository(
                db, clock).GetAsync(run.Id, plan.Id, default);
            Assert.NotNull(stale);
            Assert.False(stale.IsCurrent);
            clock.Advance(TimeSpan.FromSeconds(2));

            // F-097c.4c.0a: source-locked historical mapping rereview
            // eligibility can be SHA-verified for the LATER epoch without
            // converting F-029 back to Current or reading GitHub.
            Assert.Equal("CheckpointTargetReReviewDisabled",
                (await Assert.ThrowsAsync<GitCheckpointTargetReReviewException>(
                    () => ReReview(db, clock, false).ReadAsync(
                        run.Id, plan.Id, later.Id, "repo-1",
                        actor, default))).Code);
            var candidate = await ReReview(db, clock).ReadAsync(
                run.Id, plan.Id, later.Id, "repo-1", actor, default);
            Assert.Equal(1, candidate.EvidenceVersion);
            Assert.Equal(plan.SnapshotId, candidate.BaselineSnapshotId);
            Assert.Equal(laterId, candidate.SourceSnapshotId);
            Assert.Equal(later.Id, candidate.CheckpointId);
            Assert.Equal(verified.EvidenceSha256,
                candidate.SourceFullRefProofSha256);
            Assert.Equal(verified.SourceManifestRootSha256,
                candidate.SourceManifestRootSha256);
            Assert.Equal(oidLength == 40 ? "sha1" : "sha256",
                candidate.ObjectFormat);
            Assert.Equal(2, candidate.SourceRefCount);
            Assert.Equal("migration-one", candidate.TargetRepositoryName);
            Assert.False(candidate.CanObserveTarget || candidate.CanPush ||
                candidate.CanRetry || candidate.CanDispatch ||
                candidate.MigrationComplete);
            Assert.Equal(64, candidate.EvidenceSha256.Length);
            var originalReviewer = (await db.IncrementalReviews
                .AsNoTracking().SingleAsync(x => x.PreparationId == run.Id))
                .ReviewerId!.Value;
            GitCheckpointTargetReReviewIntegrity.Verify(
                candidate, stale, later, verified, originalReviewer);
            Assert.Equal("CheckpointTargetReReviewEvidenceInvalid",
                Assert.Throws<GitCheckpointTargetReReviewException>(() =>
                    GitCheckpointTargetReReviewIntegrity.Verify(
                        candidate with { CanObserveTarget = true },
                        stale, later, verified, originalReviewer)).Code);
            Assert.Equal("CheckpointTargetReReviewHistoricalReviewInvalid",
                (await Assert.ThrowsAsync<GitCheckpointTargetReReviewException>(
                    () => ReReview(db, clock).ReadAsync(
                        run.Id, plan.Id, later.Id, "repo-1",
                        Guid.NewGuid(), default))).Code);
            Assert.Equal("CheckpointTargetReReviewSourceUnverified",
                (await Assert.ThrowsAsync<GitCheckpointTargetReReviewException>(
                    () => ReReview(db, clock).ReadAsync(
                        run.Id, plan.Id, later.Id, "foreign-repo",
                        actor, default))).Code);

            var observation = GitHubRefObservationIntegrity.Seal(
                plan.Organization, "migration-one", clock.GetUtcNow(),
                [new("refs/heads/main",
                    new string('a', oidLength), "commit")]);
            var destination = await db.Connections.AsNoTracking()
                .SingleAsync(x => x.Id == plan.DestinationConnectionId);
            var rejectedTarget = await Assert.ThrowsAsync<
                GitHubTargetRefEvidenceException>(() =>
                new PostgresGitHubTargetRefEvidenceStore(db, clock)
                    .SaveAsync(plan, "repo-1", actor,
                        destination.ValidatedAtUtc, observation, default));
            Assert.Equal("RefEvidenceContextChanged", rejectedTarget.Code);

            Assert.Equal("CheckpointWorkpackDisabled",
                (await Assert.ThrowsAsync<
                    GitIncrementalCheckpointWorkpackException>(() =>
                    Workpacks(db, clock, false).CaptureAsync(
                        run.Id, plan.Id, later.Id, Guid.NewGuid(),
                        "repo-1", new string('f', 64), actor, default))).Code);
            Assert.Equal("CheckpointWorkpackSourceTargetUnverified",
                (await Assert.ThrowsAsync<
                    GitIncrementalCheckpointWorkpackException>(() =>
                    Workpacks(db, clock).CaptureAsync(
                        run.Id, plan.Id, later.Id, Guid.NewGuid(),
                        "repo-1", new string('f', 64), actor, default))).Code);
            Assert.Empty(await db.IncrementalGitRefReconciliations
                .AsNoTracking().Where(x => x.SnapshotId == laterId)
                .ToArrayAsync());
        }

        await using (var restarted = fixture.NewContext())
        {
            var reviewed = await ReReview(restarted, clock).ReadAsync(
                run.Id, plan.Id,
                (await restarted.GitHubSourceSyncCheckpoints.AsNoTracking()
                    .SingleAsync(x => x.PlanId == plan.Id &&
                        x.SnapshotId == laterId)).Id,
                "repo-1", actor, default);
            Assert.Equal(laterId, reviewed.SourceSnapshotId);
            Assert.False(reviewed.CanObserveTarget);

            // A historical review cannot outlive its previously
            // validated destination connection; this witness is not
            // an evergreen permit after staging advances.
            clock.Advance(TimeSpan.FromMinutes(6));
            Assert.Equal("CheckpointTargetReReviewDestinationValidationExpired",
                (await Assert.ThrowsAsync<GitCheckpointTargetReReviewException>(
                    () => ReReview(restarted, clock).ReadAsync(
                        run.Id, plan.Id,
                        reviewed.CheckpointId, "repo-1", actor, default))).Code);
            // Revocation is fail-closed independently of the expiry
            // guard, even with a previously verified F-097 chain.
            var reviewRow = await restarted.IncrementalReviews
                .SingleAsync(x => x.PreparationId == run.Id);
            reviewRow.State = IncrementalReviewStates.Revoked;
            await restarted.SaveChangesAsync();
            Assert.Equal("CheckpointTargetReReviewHistoricalReviewInvalid",
                (await Assert.ThrowsAsync<GitCheckpointTargetReReviewException>(
                    () => ReReview(restarted, clock).ReadAsync(
                        run.Id, plan.Id,
                        reviewed.CheckpointId, "repo-1", actor, default))).Code);
            Assert.Empty(await restarted.IncrementalGitRefReconciliations
                .AsNoTracking().Where(x => x.SnapshotId == laterId)
                .ToArrayAsync());
            Assert.Empty(await restarted.GitIncrementalCheckpointWorkpacks
                .AsNoTracking().Where(x => x.CheckpointId != checkpoint.Id &&
                    x.PlanId == plan.Id).ToArrayAsync());
            Assert.Empty(await restarted.GitTransferWorkpacks.AsNoTracking()
                .Where(x => x.PlanId == plan.Id).ToArrayAsync());
        }
    }

    [Theory]
    [InlineData(40)]
    [InlineData(64)]
    public async Task F097c3_F061_Git_mirror_is_joined_to_immutable_checkpoint_and_workpack(
        int oidLength)
    {
        var clock = new PostgresIncrementalOperationPlanTests.ManualClock(
            DateTimeOffset.UtcNow);
        IncrementalPreparationView run;
        IncrementalOperationPlan plan;
        GitHubSourceSyncCheckpointReceipt checkpoint;
        GitHubTargetRefEvidenceView target;
        GitHubSourceCheckpointGitRefProof proof;
        Guid workpackId;
        Guid mirrorId;

        await using (var db = fixture.NewContext())
        {
            var setup = await SetupAsync(db, clock, oidLength);
            (run, plan, checkpoint, _, _) = setup;
            var actor = setup.ActorId;
            clock.Advance(TimeSpan.FromSeconds(2));
            var observation = GitHubRefObservationIntegrity.Seal(
                plan.Organization, "migration-one", clock.GetUtcNow(),
                [new("refs/heads/main", new string('a', oidLength), "commit")]);
            var dest = await db.Connections.AsNoTracking()
                .SingleAsync(x => x.Id == plan.DestinationConnectionId);
            target = await new PostgresGitHubTargetRefEvidenceStore(db, clock)
                .SaveAsync(plan, "repo-1", actor,
                    dest.ValidatedAtUtc, observation, default);
            proof = await Ledger(db, clock).CaptureAsync(
                run.Id, plan.Id, checkpoint.Id, "repo-1", actor, default);
            var reader = Physical(db, clock);

            Assert.Equal("GitCheckpointMirrorPreflightDisabled",
                (await Assert.ThrowsAsync<GitIncrementalAncestryLedgerException>(
                    () => Physical(db, clock, false).ReadAsync(
                        run.Id, plan.Id, checkpoint.Id, target.Id,
                        "repo-1", target.EvidenceSha256, Guid.NewGuid(),
                        default))).Code);
            Assert.Equal("GitAncestryLedgerMirrorNotVerified",
                (await Assert.ThrowsAsync<GitIncrementalAncestryLedgerException>(
                    () => reader.ReadAsync(
                        run.Id, plan.Id, checkpoint.Id, target.Id,
                        "repo-1", target.EvidenceSha256, Guid.NewGuid(),
                        default))).Code);

            var refs = proof.Refs;
            var compared = GitMirrorRefReconciler.Compare(new(
                plan.Id, plan.ManifestSha256, proof.SourceEvidenceSha256,
                target.EvidenceSha256, refs, observation.AsMirrorRefs()));
            var now = clock.GetUtcNow();
            workpackId = Guid.NewGuid();
            var reconciliationId = Guid.NewGuid();
            mirrorId = GitSourceSnapshotMirrorIntegrity.PhysicalMirrorId(
                proof.SnapshotId, workpackId);
            var bundleSha = new string('e', 64);
            var closureSha = new string('d', 64);
            var refsSha = GitSourceSnapshotMirrorIntegrity.SourceRefsSha(refs);
            var mirrorSha = GitSourceSnapshotMirrorIntegrity.MirrorEvidenceSha(
                proof.SnapshotId, mirrorId, workpackId,
                bundleSha, proof.SourceEvidenceSha256, refsSha,
                refs.Count, closureSha, 3);

            db.IncrementalGitRefReconciliations.Add(new()
            {
                Id = reconciliationId, PlanId = plan.Id,
                SnapshotId = proof.SnapshotId,
                SourceRepositoryId = "repo-1",
                TargetRepositoryName = "migration-one",
                TargetObservationId = target.Id,
                ManifestSha256 = plan.ManifestSha256,
                SourceEvidenceSha256 = proof.SourceEvidenceSha256,
                SourceManifestRootSha256 = proof.SourceManifestRootSha256,
                TargetEvidenceSha256 = target.EvidenceSha256,
                ReconciliationSha256 = compared.ReconciliationSha256,
                CreatedAtUtc = now, CreatedBy = actor,
                SourceRefCount = compared.SourceRefCount,
                TargetRefCount = compared.TargetRefCount,
                MatchingCount = compared.MatchingCount,
                CreateCandidateCount = compared.CreateCandidateCount,
                DivergedCount = compared.DivergedCount,
                TargetOnlyCount = compared.TargetOnlyCount,
                ItemsJson = JsonSerializer.Serialize(compared.Items),
                ReadyForExecution = false
            });
            await db.SaveChangesAsync();

            db.GitTransferWorkpacks.Add(new()
            {
                Id = workpackId, ReconciliationId = reconciliationId,
                PlanId = plan.Id, SourceConnectionId = plan.SourceConnectionId,
                SourceRepositoryId = "repo-1",
                TargetRepositoryName = "migration-one",
                ManifestSha256 = plan.ManifestSha256,
                ReconciliationSha256 = compared.ReconciliationSha256,
                TargetEvidenceSha256 = target.EvidenceSha256,
                CreatedBy = actor,
                State = GitTransferWorkpackStates.Blocked,
                Phase = GitTransferWorkpackPhases.WriteBlocked,
                Attempts = 0, MaxAttempts = 5,
                AvailableAtUtc = now, CreatedAtUtc = now,
                UpdatedAtUtc = now, BlockedAtUtc = now,
                LastErrorCode = "DestinationWritesNotAuthorized",
                ReadyForExecution = false
            });
            await db.SaveChangesAsync();
            db.GitSourceBundleEvidence.Add(new()
            {
                WorkpackId = workpackId, PlanId = plan.Id,
                ReconciliationId = reconciliationId,
                SourceRepositoryId = "repo-1",
                SourceEvidenceSha256 = proof.SourceEvidenceSha256,
                ReconciliationSha256 = compared.ReconciliationSha256,
                BundleFileName = workpackId.ToString("N") + ".bundle",
                BundleSha256 = bundleSha, BundleBytes = 1234,
                RefCount = refs.Count, AcquiredAtUtc = now,
                ReadyForExecution = false
            });
            db.GitSourceMirrorLifecycles.Add(new()
            {
                MirrorId = mirrorId,
                SourceSnapshotId = proof.SnapshotId,
                WorkpackId = workpackId, PlanId = plan.Id,
                ReconciliationId = reconciliationId,
                BundleSha256 = bundleSha,
                SourceEvidenceSha256 = proof.SourceEvidenceSha256,
                RefsSha256 = refsSha,
                State = GitSourceMirrorLifecycleStates.Verified,
                LeaseGeneration = 1,
                ObjectSetSha256 = closureSha, ObjectCount = 3,
                MirrorProofSha256 = mirrorSha,
                VerifiedAtUtc = now.AddSeconds(-1),
                CreatedAtUtc = now.AddSeconds(-3),
                UpdatedAtUtc = now
            });
            await db.SaveChangesAsync();
            db.GitSourceMirrorLifecycleEvents.Add(new()
            {
                MirrorId = mirrorId, Type = "Verified",
                Generation = 1, WorkerId = Guid.NewGuid(),
                OccurredAtUtc = now
            });
            await db.SaveChangesAsync();

            var verified = await reader.ReadAsync(
                run.Id, plan.Id, checkpoint.Id, target.Id,
                "repo-1", target.EvidenceSha256, workpackId, default);
            Assert.Equal(1, verified.EvidenceVersion);
            Assert.Equal(proof.SnapshotId,
                verified.SourceAndTarget.SourceSnapshotId);
            Assert.Equal(proof.EvidenceSha256,
                verified.SourceAndTarget.SourceFullRefProofSha256);
            Assert.Equal(mirrorId, verified.PhysicalMirror.MirrorId);
            Assert.Equal(closureSha, verified.PhysicalMirror.ObjectSetSha256);
            Assert.False(verified.CanPush || verified.CanRetry ||
                verified.CanDispatch || verified.MigrationComplete);
            Assert.Equal(0, await db.GitIncrementalAncestryLedgers
                .AsNoTracking().CountAsync(x => x.CheckpointId == checkpoint.Id));
        }

        await using (var db = fixture.NewContext())
        {
            var reader = Physical(db, clock);
            var verified = await reader.ReadAsync(
                run.Id, plan.Id, checkpoint.Id, target.Id,
                "repo-1", target.EvidenceSha256, workpackId, default);
            Assert.Equal(mirrorId, verified.PhysicalMirror.MirrorId);
            Assert.Equal("GitAncestryLedgerMirrorNotVerified",
                (await Assert.ThrowsAsync<GitIncrementalAncestryLedgerException>(
                    () => reader.ReadAsync(
                        run.Id, plan.Id, checkpoint.Id, target.Id,
                        "repo-1", target.EvidenceSha256, Guid.NewGuid(),
                        default))).Code);
            Assert.Equal("GitCheckpointMirrorSourceTargetUnverified",
                (await Assert.ThrowsAsync<GitIncrementalAncestryLedgerException>(
                    () => reader.ReadAsync(
                        run.Id, plan.Id, checkpoint.Id, target.Id,
                        "repo-1", new string('f', 64), workpackId,
                        default))).Code);

            // The F-061 SQL guard must also reject a direct mutation of
            // the sealed source ref-set hash; the read-side never repairs
            // or silently replaces a physical proof.
            var refused = await Assert.ThrowsAsync<Npgsql.PostgresException>(
                () => db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE git_source_mirror_lifecycles
                    SET refs_sha256 = {new string('f', 64)}
                    WHERE mirror_id = {mirrorId}
                    """));
            Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException,
                refused.SqlState);
        }
    }

    [Theory]
    [InlineData(40)]
    [InlineData(64)]
    public async Task F097c2_exact_F014_F091_F097_F031_preflight_is_read_only_and_restart_safe(
        int oidLength)
    {
        var clock = new PostgresIncrementalOperationPlanTests.ManualClock(
            DateTimeOffset.UtcNow);
        IncrementalPreparationView preparation;
        IncrementalOperationPlan plan;
        GitHubSourceSyncCheckpointReceipt checkpoint;
        Guid actor;
        Guid sourceJob;
        GitHubTargetRefEvidenceView target;
        GitHubSourceCheckpointGitRefProof proof;
        await using (var db = fixture.NewContext())
        {
            (preparation, plan, checkpoint, sourceJob, actor) =
                await SetupAsync(db, clock, oidLength);
            var ledger = Ledger(db, clock);

            clock.Advance(TimeSpan.FromSeconds(2));
            var observation = GitHubRefObservationIntegrity.Seal(
                plan.Organization, "migration-one", clock.GetUtcNow(),
                [new("refs/heads/main", new string('a', oidLength), "commit")]);
            var destination = await db.Connections.AsNoTracking()
                .SingleAsync(x => x.Id == plan.DestinationConnectionId);
            target = await new PostgresGitHubTargetRefEvidenceStore(db, clock)
                .SaveAsync(plan, "repo-1", actor,
                    destination.ValidatedAtUtc, observation, default);
            Assert.True(target.IsCurrent);

            var preflight = Preflight(db, clock);
            Assert.Equal("GitCheckpointAuditPreflightDisabled",
                (await Assert.ThrowsAsync<
                    GitIncrementalCheckpointPreflightException>(() =>
                    Preflight(db, clock, false).ReadAsync(
                        preparation.Id, plan.Id, checkpoint.Id, target.Id,
                        "repo-1", target.EvidenceSha256, default))).Code);
            Assert.Equal("GitCheckpointAuditPreflightSourceProofMissing",
                (await Assert.ThrowsAsync<
                    GitIncrementalCheckpointPreflightException>(() =>
                    preflight.ReadAsync(preparation.Id, plan.Id,
                        checkpoint.Id, target.Id, "repo-1",
                        target.EvidenceSha256, default))).Code);

            proof = await ledger.CaptureAsync(preparation.Id, plan.Id,
                checkpoint.Id, "repo-1", actor, default);
            var read = await preflight.ReadAsync(
                preparation.Id, plan.Id, checkpoint.Id, target.Id,
                "repo-1", target.EvidenceSha256, default);
            Assert.Equal(1, read.EvidenceVersion);
            Assert.Equal(checkpoint.Id, read.SourceCheckpointId);
            Assert.Equal(sourceJob, read.SourceSnapshotId);
            Assert.Equal(proof.EvidenceSha256, read.SourceFullRefProofSha256);
            Assert.Equal(target.EvidenceSha256, read.TargetEvidenceSha256);
            Assert.Equal(oidLength == 40 ? "sha1" : "sha256",
                read.GitObjectFormat);
            Assert.Equal(proof.Refs.ToArray(), read.ExpectedSourceRefs.ToArray());
            Assert.Single(read.ExpectedTargetRefs);
            Assert.Equal(0, await db.GitIncrementalAncestryLedgers
                .AsNoTracking().CountAsync(x => x.CheckpointId == checkpoint.Id));
            Assert.False(read.CanPush);
            Assert.False(read.CanRetry);
            Assert.False(read.CanDispatch);
            Assert.False(read.MigrationComplete);
        }

        await using (var restarted = fixture.NewContext())
        {
            var preflight = Preflight(restarted, clock);
            var read = await preflight.ReadAsync(
                preparation.Id, plan.Id, checkpoint.Id, target.Id,
                "repo-1", target.EvidenceSha256, default);
            Assert.Equal(proof.EvidenceSha256, read.SourceFullRefProofSha256);
            Assert.Equal("GitCheckpointAuditPreflightTargetStale",
                (await Assert.ThrowsAsync<
                    GitIncrementalCheckpointPreflightException>(() =>
                    preflight.ReadAsync(preparation.Id, plan.Id, checkpoint.Id,
                        target.Id, "repo-1", new string('f', 64),
                        default))).Code);
            Assert.Equal("GitCheckpointAuditPreflightSourceProofMissing",
                (await Assert.ThrowsAsync<
                    GitIncrementalCheckpointPreflightException>(() =>
                    preflight.ReadAsync(preparation.Id, plan.Id, checkpoint.Id,
                        target.Id, "repo-2", target.EvidenceSha256,
                        default))).Code);
            Assert.Equal("GitCheckpointAuditPreflightCheckpointNotFound",
                (await Assert.ThrowsAsync<
                    GitIncrementalCheckpointPreflightException>(() =>
                    preflight.ReadAsync(preparation.Id, plan.Id, Guid.NewGuid(),
                        target.Id, "repo-1", target.EvidenceSha256,
                        default))).Code);

            // A new independently sealed F-031 target snapshot in the
            // WRONG Git object format is not a valid F-094 workpack.
            clock.Advance(TimeSpan.FromSeconds(2));
            var mismatched = GitHubRefObservationIntegrity.Seal(
                plan.Organization, "migration-one", clock.GetUtcNow(),
                [new("refs/heads/main",
                    new string('d', oidLength == 40 ? 64 : 40), "commit")]);
            var dest = await restarted.Connections.AsNoTracking()
                .SingleAsync(x => x.Id == plan.DestinationConnectionId);
            var alteredTarget = await new PostgresGitHubTargetRefEvidenceStore(
                restarted, clock).SaveAsync(
                    plan, "repo-1", actor, dest.ValidatedAtUtc,
                    mismatched, default);
            Assert.True(alteredTarget.IsCurrent);
            Assert.Equal("GitCheckpointAuditPreflightEvidenceInvalid",
                (await Assert.ThrowsAsync<
                    GitIncrementalCheckpointPreflightException>(() =>
                    preflight.ReadAsync(preparation.Id, plan.Id, checkpoint.Id,
                        alteredTarget.Id, "repo-1",
                        alteredTarget.EvidenceSha256, default))).Code);

            // A tampered durable F-014 source row invalidates the
            // previously recorded F-097b hash even after a restart.
            // Check this BEFORE advancing the plan's freshness deadline.
            await restarted.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE discovery_ref_observations
                SET fingerprint = {"tampered"}
                WHERE job_id = {sourceJob}
                """);
            Assert.Equal("GitCheckpointAuditPreflightSourceCorrupt",
                (await Assert.ThrowsAsync<
                    GitIncrementalCheckpointPreflightException>(() =>
                    preflight.ReadAsync(preparation.Id, plan.Id, checkpoint.Id,
                        alteredTarget.Id, "repo-1",
                        alteredTarget.EvidenceSha256, default))).Code);

            // The F-029 plan expires no later than the F-031 observation:
            // after six minutes it is correct to reject the stale PLAN
            // before even considering the older target or source proof.
            clock.Advance(TimeSpan.FromMinutes(6));
            Assert.Equal("GitCheckpointAuditPreflightPlanNotCurrent",
                (await Assert.ThrowsAsync<
                    GitIncrementalCheckpointPreflightException>(() =>
                    preflight.ReadAsync(preparation.Id, plan.Id, checkpoint.Id,
                        alteredTarget.Id, "repo-1",
                        alteredTarget.EvidenceSha256, default))).Code);
        }
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
