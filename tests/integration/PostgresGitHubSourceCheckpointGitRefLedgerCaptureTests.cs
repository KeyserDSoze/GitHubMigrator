using System.Text.Json;
using System.Diagnostics;
using System.Security.Cryptography;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.PostgreSql;
using GitHubMigrator.Infrastructure.GitObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Real F-008/F-014 -> F-013 -> F-026/029 -> F-090/091 -> F-097b,
// including restart/replay. No stubs for source, plan or checkpoint proofs.
public sealed class PostgresGitHubSourceCheckpointGitRefLedgerCaptureTests(
    PostgresDraftFixture fixture, Xunit.Abstractions.ITestOutputHelper output)
    : IClassFixture<PostgresDraftFixture>, IAsyncLifetime
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
        await GitCheckpointTargetHumanReviewSchema.InitializeAsync(services);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // #124 cancellation injected at the exact physical -> PostgreSQL
    // handoff, AFTER the kernel move and two complete quarantined
    // fsck/ref/OID passes. A callback that ignores CancellationToken
    // must never be entered on an already-cancelled operation.
    private sealed class CancelAtPhysicalHandoffClock(
        CancellationTokenSource cancellation) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            cancellation.Cancel();
            return DateTimeOffset.UtcNow;
        }
    }

    // #131: the physical import/failure/restart fixture is deliberately
    // longer than the five-minute F-031 target-observation lease when
    // CI runs real SHA1/SHA256 Git subprocesses and serial SQL checks.
    // Keep production expiry EXACTLY unchanged. For only the two
    // failAfterImport=true cases, let the test host clock track real UTC
    // until near the already committed F-031 TTL, then clamp it WITHIN
    // that same window. A separate real system-clock fixture must still
    // exercise stale-proof refusal. This is not a runtime clock or a
    // source refresh: immutable SHA, locked DB, Git fsck remain real.
    private sealed class LongPhysicalFixtureClock : TimeProvider
    {
        private long latestUtcTicks = DateTimeOffset.MaxValue.UtcDateTime.Ticks;

        public override DateTimeOffset GetUtcNow()
        {
            var utc = DateTimeOffset.UtcNow;
            var maxTicks = Interlocked.Read(ref latestUtcTicks);
            return utc.UtcDateTime.Ticks <= maxTicks
                ? utc
                : new DateTimeOffset(maxTicks, TimeSpan.Zero);
        }

        public void BoundToOriginalObservation(DateTimeOffset observedAtUtc)
        {
            if (observedAtUtc.Offset != TimeSpan.Zero)
                throw new ArgumentException(
                    "F-031 integration observation must be UTC");
            // Also leave a minute for F-031's separate destination-
            // validation window, which may predate the observation.
            // No source permission TTL or PostgreSQL time is extended.
            var withinWindow = observedAtUtc.AddMinutes(4);
            Interlocked.Exchange(ref latestUtcTicks,
                withinWindow.UtcDateTime.Ticks);
        }
    }

    // Actual local Git mirror with object IDs bound to F-097b. Only
    // the test provisions this trusted root; no HTTP path or token.
    private sealed class CheckpointLocalMirror : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(),
            "f035v2-checkpoint-" + Guid.NewGuid().ToString("N"));
        public string SourceRoot => Path.Combine(root, "sources");
        public string BundleRoot => Path.Combine(root, "bundles");
        public string VerifiedMirrorRoot => Path.Combine(root, "later-mirrors");
        public IReadOnlyList<GitMirrorRef> Refs { get; }

        public CheckpointLocalMirror(Guid sourceId, int oidLength)
        {
            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(BundleRoot);
            Directory.CreateDirectory(VerifiedMirrorRoot);
            var work = Path.Combine(root, "working");
            if (oidLength == 64)
                Git("init", "--quiet", "--object-format=sha256", work);
            else
                Git("init", "--quiet", work);
            Git("-C", work, "config", "user.email",
                "checkpoint-integration@example.invalid");
            Git("-C", work, "config", "user.name", "Checkpoint Test");
            File.WriteAllText(Path.Combine(work, "README.md"),
                "source-only checkpoint binary bundle\n");
            Git("-C", work, "add", "README.md");
            Git("-C", work, "commit", "--quiet", "-m", "checkpoint-epoch");
            Git("-C", work, "branch", "-M", "main");
            Git("-C", work, "tag", "v1");
            var bare = LocalGitSourceBundleAcquirer.LocalMirrorDirectory(
                SourceRoot, sourceId, "repo-1");
            Directory.CreateDirectory(Path.GetDirectoryName(bare)!);
            Git("clone", "--bare", "--quiet", work, bare);
            Refs = [
                new GitMirrorRef("refs/heads/main",
                    Git("--git-dir=" + bare, "rev-parse", "refs/heads/main")),
                new GitMirrorRef("refs/tags/v1",
                    Git("--git-dir=" + bare, "rev-parse", "refs/tags/v1"))
            ];
            Assert.All(Refs, gitRef =>
                Assert.Equal(oidLength, gitRef.ObjectId.Length));
        }

        private static string Git(params string[] args)
        {
            using var p = new Process();
            p.StartInfo.FileName = "/usr/bin/git";
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;
            p.StartInfo.UseShellExecute = false;
            foreach (var arg in args) p.StartInfo.ArgumentList.Add(arg);
            Assert.True(p.Start());
            var output = p.StandardOutput.ReadToEnd();
            var error = p.StandardError.ReadToEnd();
            Assert.True(p.WaitForExit(30_000), "Local Git fixture timed out");
            Assert.True(p.ExitCode == 0, error);
            return output.Trim();
        }

        public void Dispose()
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }



    // #120 real local-import wrapper for cross-DbContext heartbeat tests:
    // delay only the sandbox fixture; the real importer still performs
    // actual immutable bundle import + Git OID/fsck/closed-graph checks.
    private sealed class DelayedVerifiedImporter(
        IGitIncrementalLaterCheckpointMirrorImporter inner,
        bool failAfterDelay, bool failAfterImport,
        Func<Guid, CancellationToken, Task> waitBeforeImport)
        : IGitIncrementalLaterCheckpointMirrorImporter
    {
        public async Task<GitIncrementalLaterCheckpointMirrorOfflineAudit>
            MaterializeLaterCheckpointAsync(
                GitIncrementalLaterCheckpointMirrorStagingPlan stage,
                GitIncrementalLaterCheckpointBundleReceipt durable,
                CancellationToken ct)
        {
            // Successful worker tests must observe *actual* repeated
            // heartbeat commits before Git is released; a fixed sleep
            // is not deterministic under loaded PostgreSQL CI runners.
            await waitBeforeImport(stage.PhysicalMirrorId, ct);
            if (failAfterDelay)
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorWorkerInjectedImportFailure");
            var materialized = await inner.MaterializeLaterCheckpointAsync(
                stage, durable, ct);
            if (failAfterImport)
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorWorkerInjectedPostImportFailure");
            return materialized;
        }
    }

    // #120: simulate a long-running local source importer whose Git
    // operations remain cancellation-aware. A second PostgreSQL replica
    // can revoke the exact held generation during the pending import.
    private sealed class CancellableMirrorImporter
        : IGitIncrementalLaterCheckpointMirrorImporter
    {
        private readonly TaskCompletionSource<bool> entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => entered.Task;

        public async Task<GitIncrementalLaterCheckpointMirrorOfflineAudit>
            MaterializeLaterCheckpointAsync(
                GitIncrementalLaterCheckpointMirrorStagingPlan stage,
                GitIncrementalLaterCheckpointBundleReceipt durable,
                CancellationToken ct)
        {
            entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException(
                "Cancelled importer must never return an audit");
        }
    }

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
        IncrementalOperationPlan plan, int oidLength,
        IReadOnlyList<GitMirrorRef>? physicalSourceRefs = null)
    {
        var jobs = new PostgresMigrationJobRepository(db, clock);
        var job = await jobs.EnqueueDiscoveryAsync(
            plan.SourceConnectionId, Guid.NewGuid(), Guid.NewGuid(), default);
        var lease = (await jobs.ClaimForSourceAsync(
            plan.SourceConnectionId, Guid.NewGuid(),
            TimeSpan.FromMinutes(3), default))!;
        Assert.Equal(job.Id, lease.Id);
        var observedAt = clock.GetUtcNow().AddMinutes(2);
        var refs = physicalSourceRefs is null
            ? new[] {
                new RepositoryRef("repo-1", "refs/heads/main",
                    new string('c', oidLength)),
                new RepositoryRef("repo-1", "refs/tags/v1",
                    new string('b', oidLength))
            }
            : physicalSourceRefs.Select(x => new RepositoryRef(
                "repo-1", x.Name, x.ObjectId)).ToArray();
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

    // Two genuine F-014/F-091/F-097b epochs traverse the production API,
    // JWT role gate and persisted human journal (not a synthetic receipt).
    // The reviewer identity comes from JWT claims, even when the caller
    // tries to smuggle two different actor UUIDs into the JSON request.
    [Theory]
    [InlineData(40, GitCheckpointTargetHumanReviewStates.ApprovedForObservation, false, false, false, false, false, false, false)]
    [InlineData(64, GitCheckpointTargetHumanReviewStates.ApprovedForObservation, false, false, false, false, false, false, false)]
    [InlineData(64, GitCheckpointTargetHumanReviewStates.Rejected, false, false, false, false, false, false, false)]
    [InlineData(40, GitCheckpointTargetHumanReviewStates.ApprovedForObservation, true, false, false, false, false, false, false)]
    [InlineData(64, GitCheckpointTargetHumanReviewStates.ApprovedForObservation, true, false, false, false, false, false, false)]
    [InlineData(40, GitCheckpointTargetHumanReviewStates.ApprovedForObservation, true, true, false, false, false, false, false)]
    [InlineData(40, GitCheckpointTargetHumanReviewStates.ApprovedForObservation, true, false, true, false, false, false, false)]
    [InlineData(64, GitCheckpointTargetHumanReviewStates.ApprovedForObservation, true, false, true, false, false, false, false)]
    [InlineData(40, GitCheckpointTargetHumanReviewStates.ApprovedForObservation, true, false, true, true, false, false, false)]
    [InlineData(64, GitCheckpointTargetHumanReviewStates.ApprovedForObservation, true, false, true, true, false, false, false)]
    [InlineData(40, GitCheckpointTargetHumanReviewStates.ApprovedForObservation, true, false, true, false, true, false, false)]
    [InlineData(40, GitCheckpointTargetHumanReviewStates.ApprovedForObservation, true, false, true, false, false, true, false)]
    [InlineData(64, GitCheckpointTargetHumanReviewStates.ApprovedForObservation, true, false, true, false, false, true, false)]
    // #131: separate fresh F-031 source epochs for adversarial
    // pre-isolation denial and post-isolation physical review.
    [InlineData(40, GitCheckpointTargetHumanReviewStates.ApprovedForObservation, true, false, true, false, false, true, true)]
    [InlineData(64, GitCheckpointTargetHumanReviewStates.ApprovedForObservation, true, false, true, false, false, true, true)]
    public async Task F097c4c0c_authenticated_http_review_of_real_later_checkpoint_is_immutable(
        int oidLength, string decision, bool verifyPhysical,
        bool corruptPhysical, bool runWorker, bool failWorker,
        bool fenceWorker, bool failAfterImport, bool preMoveOnly)
    {
        // #131: per-phase real-source TTL budget for long Git tests.
        // Diagnostic-only: no skip, altered clock, SQL or Git shortcut.
        var phaseTimer = Stopwatch.StartNew();
        void Phase(string stage, DateTimeOffset? observedAtUtc = null)
        {
            if (!failAfterImport) return;
            var age = observedAtUtc is null ? "not-yet-observed"
                : (DateTimeOffset.UtcNow - observedAtUtc.Value)
                    .TotalSeconds.ToString("F1",
                        System.Globalization.CultureInfo.InvariantCulture);
            output.WriteLine($"#131 SHA{oidLength} {stage}: " +
                $"elapsed={phaseTimer.Elapsed.TotalSeconds:F1}s; " +
                $"F031-real-age={age}s");
        }

        var clock = new PostgresIncrementalOperationPlanTests.ManualClock(
            DateTimeOffset.UtcNow);
        CheckpointLocalMirror? physicalMirror = null;
        IncrementalPreparationView run;
        IncrementalOperationPlan plan;
        GitHubSourceSyncCheckpointReceipt later;
        GitCheckpointTargetReReviewCandidate candidate;
        await using (var db = fixture.NewContext())
        {
            var setup = await SetupAsync(db, clock, oidLength);
            run = setup.Preparation;
            plan = setup.Plan;
            physicalMirror = new CheckpointLocalMirror(
                plan.SourceConnectionId, oidLength);
            await Ledger(db, clock).CaptureAsync(
                run.Id, plan.Id, setup.Checkpoint.Id,
                "repo-1", setup.ActorId, default);
            var laterSnapshot = await LaterF014SnapshotAsync(
                db, clock, plan, oidLength, physicalMirror.Refs);
            later = await new PostgresGitHubSourceSyncCheckpointJournal(
                db, clock, new(true),
                new PostgresIncrementalOperationPlanRepository(db, clock),
                new PostgresStagingRepository(db, clock)).CaptureAsync(
                    run.Id, plan.Id, laterSnapshot, setup.Checkpoint.Id,
                    plan.ManifestSha256, setup.ActorId, default);
            await Ledger(db, clock).CaptureAsync(
                run.Id, plan.Id, later.Id, "repo-1",
                setup.ActorId, default);
            candidate = await ReReview(db, clock).ReadAsync(
                run.Id, plan.Id, later.Id, "repo-1",
                setup.ActorId, default);
            Assert.NotEqual(candidate.BaselineSnapshotId,
                candidate.SourceSnapshotId);
            Assert.Equal(oidLength == 40 ? "sha1" : "sha256",
                candidate.ObjectFormat);
        }

        // Only the two deliberately long post-import/restart scenarios
        // use a deterministic TEST-HOST clock; all other theory cases
        // continue testing real wall-clock provenance expiry.
        var longPhysicalClock =
            failAfterImport ? new LongPhysicalFixtureClock() : null;
        Phase("source/refs seeded");
        var api = new ApiHarness {
            IntegrationClockOverride = longPhysicalClock,
            EnableCheckpointHumanReview = true,
            EnableCheckpointTargetReadPreflight = true,
            EnableCheckpointTargetObservation = true,
            EnableLaterCheckpointAuditRead = true,
            EnableLaterCheckpointWorkpack = true,
            EnableLaterCheckpointHistory = true,
            EnableLaterCheckpointBundleRead = true,
            EnableLaterCheckpointBundleLease = true,
            EnableLaterCheckpointLocalBundle = true,
            EnableLaterCheckpointDurableBundle = true,
            EnableLaterCheckpointMirrorStaging = true,
            EnableLaterCheckpointMirrorAdmission = true,
            EnableLaterCheckpointMirrorLease = true,
            EnableLaterCheckpointMirrorVerified = verifyPhysical,
            EnableLaterCheckpointMirrorImportWorker = runWorker,
            EnableLaterCheckpointMirrorRecoveryInspection = runWorker,
            EnableLaterCheckpointMirrorRecoveryReview = runWorker,
            TrustedLocalVerifiedMirrorRoot = physicalMirror.VerifiedMirrorRoot,
            TrustedLocalBundleSourceRoot = physicalMirror.SourceRoot,
            TrustedLocalBundleOutputRoot = physicalMirror.BundleRoot,
            TargetObjectIdLength = oidLength
        };
        try
        {
            await api.InitializeAsync();
            var token = await api.LoginAsync(
                api.SuperadminName, ApiHarness.SuperadminPassword);
            Guid reviewerId;
            var requesterName = "source-requester-" + Guid.NewGuid().ToString("N");
            var secondReviewerName = "other-reviewer-" + Guid.NewGuid().ToString("N");
            await using (var scope = api.Host.Services.CreateAsyncScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<
                    Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
                var user = await users.FindByNameAsync(api.SuperadminName);
                reviewerId = Assert.IsType<ApplicationUser>(user).Id;
                // A legitimate superadmin whose JWT subject equals the
                // original requester must not self-approve, even if the
                // JSON tries to spoof a different reviewer's UUID.
                var requesterUser = new ApplicationUser {
                    Id = candidate.RequestedBy, UserName = requesterName,
                    MustChangePassword = false
                };
                var createdRequester = await users.CreateAsync(
                    requesterUser, ApiHarness.SuperadminPassword);
                Assert.True(createdRequester.Succeeded);
                var requesterRole = await users.AddToRoleAsync(
                    requesterUser, "superadmin");
                Assert.True(requesterRole.Succeeded);
                var secondReviewer = new ApplicationUser {
                    UserName = secondReviewerName, MustChangePassword = false
                };
                var createdOther = await users.CreateAsync(
                    secondReviewer, ApiHarness.SuperadminPassword);
                Assert.True(createdOther.Succeeded);
                var otherRole = await users.AddToRoleAsync(
                    secondReviewer, "superadmin");
                Assert.True(otherRole.Succeeded);
            }
            Assert.NotEqual(candidate.RequestedBy, reviewerId);
            var requesterToken = await api.LoginAsync(
                requesterName, ApiHarness.SuperadminPassword);
            var otherReviewerToken = await api.LoginAsync(
                secondReviewerName, ApiHarness.SuperadminPassword);
            var root = "/api/incremental-preparations/" + run.Id +
                "/operation-plans/" + plan.Id +
                "/source-sync-checkpoints/" + later.Id +
                "/target-human-review";
            var query = "?sourceRepositoryId=repo-1";
            var outboundBefore = api.Calls.Count;

            using (var peek = await SendAsync(HttpMethod.Get,
                root + "/candidate" + query))
            {
                Assert.Equal(System.Net.HttpStatusCode.OK, peek.StatusCode);
                using var json = JsonDocument.Parse(
                    await peek.Content.ReadAsStringAsync());
                Assert.Equal(candidate.EvidenceSha256, json.RootElement
                    .GetProperty("evidenceSha256").GetString());
                Assert.Equal(candidate.SourceSnapshotId, json.RootElement
                    .GetProperty("sourceSnapshotId").GetGuid());
                Assert.False(json.RootElement.GetProperty(
                    "canObserveTarget").GetBoolean());
                Assert.False(json.RootElement.GetProperty(
                    "canPush").GetBoolean());
                Assert.DoesNotContain("refs/heads/", json.RootElement.GetRawText());
            }

            // Stale optimistic handle and a mismatched scope cannot
            // overwrite or append a new checkpoint-specific decision.
            using (var changed = await SendAsync(HttpMethod.Post, root,
                Request(decision, new string('0', 64))))
            {
                Assert.Equal(System.Net.HttpStatusCode.Conflict,
                    changed.StatusCode);
                Assert.Contains("CheckpointHumanReviewCandidateChanged",
                    await changed.Content.ReadAsStringAsync());
            }
            using (var wrongRepo = await SendAsync(HttpMethod.Post, root,
                new {
                    sourceRepositoryId = "repo-foreign",
                    decision,
                    expectedCandidateEvidenceSha256 = candidate.EvidenceSha256,
                    reviewedBy = candidate.RequestedBy
                }))
            {
                Assert.Equal(System.Net.HttpStatusCode.Conflict,
                    wrongRepo.StatusCode);
            }
            using (var selfReview = await SendAsync(HttpMethod.Post, root,
                Request(decision, candidate.EvidenceSha256), requesterToken))
            {
                Assert.Equal(System.Net.HttpStatusCode.Conflict,
                    selfReview.StatusCode);
                Assert.Contains("CheckpointHumanReviewIndependentReviewerRequired",
                    await selfReview.Content.ReadAsStringAsync());
            }

            Guid recordedWorkpackId = Guid.Empty;
            string receiptSha;
            Guid receiptId;
            using (var posted = await SendAsync(HttpMethod.Post, root,
                Request(decision, candidate.EvidenceSha256)))
            {
                Assert.Equal(System.Net.HttpStatusCode.OK, posted.StatusCode);
                using var json = JsonDocument.Parse(
                    await posted.Content.ReadAsStringAsync());
                var body = json.RootElement;
                receiptSha = body.GetProperty("evidenceSha256").GetString()!;
                receiptId = body.GetProperty("id").GetGuid();
                Assert.Equal(decision, body.GetProperty("decision").GetString());
                Assert.Equal(candidate.RequestedBy, body.GetProperty("requestedBy").GetGuid());
                Assert.Equal(reviewerId, body.GetProperty("reviewedBy").GetGuid());
                Assert.NotEqual(Guid.Empty, receiptId);
                foreach (var flag in new[] {
                    "canObserveTarget", "canPush", "canRetry",
                    "canDispatch", "migrationComplete"
                })
                    Assert.False(body.GetProperty(flag).GetBoolean());
            }

            using (var replay = await SendAsync(HttpMethod.Post, root,
                Request(decision, candidate.EvidenceSha256)))
            {
                Assert.Equal(System.Net.HttpStatusCode.OK, replay.StatusCode);
                using var json = JsonDocument.Parse(
                    await replay.Content.ReadAsStringAsync());
                Assert.Equal(receiptId, json.RootElement.GetProperty("id").GetGuid());
                Assert.Equal(receiptSha, json.RootElement
                    .GetProperty("evidenceSha256").GetString());
            }
            using (var differentReviewer = await SendAsync(
                HttpMethod.Post, root,
                Request(decision, candidate.EvidenceSha256), otherReviewerToken))
            {
                Assert.Equal(System.Net.HttpStatusCode.Conflict,
                    differentReviewer.StatusCode);
                Assert.Contains("CheckpointHumanReviewConflict",
                    await differentReviewer.Content.ReadAsStringAsync());
            }
            using (var conflicting = await SendAsync(HttpMethod.Post,
                root, Request(decision ==
                    GitCheckpointTargetHumanReviewStates.Rejected
                    ? GitCheckpointTargetHumanReviewStates.ApprovedForObservation
                    : GitCheckpointTargetHumanReviewStates.Rejected,
                    candidate.EvidenceSha256)))
            {
                Assert.Equal(System.Net.HttpStatusCode.Conflict,
                    conflicting.StatusCode);
                Assert.Contains("CheckpointHumanReviewConflict",
                    await conflicting.Content.ReadAsStringAsync());
            }
            using (var history = await SendAsync(HttpMethod.Get, root + query))
            {
                Assert.Equal(System.Net.HttpStatusCode.OK, history.StatusCode);
                using var json = JsonDocument.Parse(
                    await history.Content.ReadAsStringAsync());
                Assert.Equal(receiptId, json.RootElement.GetProperty("id").GetGuid());
                Assert.Equal(receiptSha, json.RootElement
                    .GetProperty("evidenceSha256").GetString());
            }
            using (var foreign = await SendAsync(HttpMethod.Get,
                root + "?sourceRepositoryId=repo-foreign"))
                Assert.Equal(System.Net.HttpStatusCode.NotFound,
                    foreign.StatusCode);

            // #109 independent GET-only preflight after #108 review.
            // Even an approved human decision grants no provider GET.
            var readUrl = root.Replace("/target-human-review",
                "/target-read-preflight") +
                "?sourceRepositoryId=repo-1" +
                "&expectedCandidateEvidenceSha256=" + candidate.EvidenceSha256 +
                "&expectedHumanReviewEvidenceSha256=" + receiptSha;
            using (var read = await SendAsync(HttpMethod.Get, readUrl))
            {
                if (decision ==
                    GitCheckpointTargetHumanReviewStates.ApprovedForObservation)
                {
                    Assert.Equal(System.Net.HttpStatusCode.OK, read.StatusCode);
                    using var json = JsonDocument.Parse(
                        await read.Content.ReadAsStringAsync());
                    var witness = json.RootElement;
                    Assert.Equal(candidate.EvidenceSha256, witness
                        .GetProperty("candidateEvidenceSha256").GetString());
                    Assert.Equal(receiptSha, witness
                        .GetProperty("humanReviewEvidenceSha256").GetString());
                    Assert.Equal(later.Id, witness
                        .GetProperty("checkpointId").GetGuid());
                    foreach (var flag in new[] {
                        "providerReadAuthorized", "canObserveTarget",
                        "canPush", "canRetry", "canDispatch", "migrationComplete"
                    })
                        Assert.False(witness.GetProperty(flag).GetBoolean());
                }
                else
                {
                    Assert.Equal(System.Net.HttpStatusCode.Conflict,
                        read.StatusCode);
                    Assert.Contains(
                        "CheckpointTargetReadPreflightHumanApprovalRequired",
                        await read.Content.ReadAsStringAsync());
                }
            }
            if (decision ==
                GitCheckpointTargetHumanReviewStates.ApprovedForObservation)
            {
                using var wrongReviewer = await SendAsync(
                    HttpMethod.Get, readUrl, null, otherReviewerToken);
                Assert.Equal(System.Net.HttpStatusCode.Conflict,
                    wrongReviewer.StatusCode);
                Assert.Contains(
                    "CheckpointTargetReadPreflightIndependentReviewerRequired",
                    await wrongReviewer.Content.ReadAsStringAsync());
                using var staleDigest = await SendAsync(
                    HttpMethod.Get, readUrl.Replace(
                        candidate.EvidenceSha256, new string('0', 64)));
                Assert.Equal(System.Net.HttpStatusCode.Conflict,
                    staleDigest.StatusCode);
                Assert.Contains("CheckpointTargetReadPreflightCandidateChanged",
                    await staleDigest.Content.ReadAsStringAsync());
            }

            // #109: a distinct, explicitly opted-in, reviewer-authenticated
            // fixed-host GET, with an immutable checkpoint-scoped F-031
            // receipt + audit. No write request is ever issued to GitHub.
            var obsUrl = root.Replace("/target-human-review",
                "/target-ref-observations");
            var targetRequest = new {
                sourceRepositoryId = "repo-1",
                expectedCandidateEvidenceSha256 = candidate.EvidenceSha256,
                expectedHumanReviewEvidenceSha256 = receiptSha,
                // Deliberately untrusted JSON; API only accepts scoped hashes.
                reviewerId = candidate.RequestedBy,
                targetUrl = "https://other.invalid/malicious",
                token = ApiHarness.TargetToken
            };
            Guid observationId = Guid.Empty;
            string? targetReceiptSha = null;
            using (var collected = await SendAsync(
                HttpMethod.Post, obsUrl, targetRequest))
            {
                if (decision ==
                    GitCheckpointTargetHumanReviewStates.ApprovedForObservation)
                {
                    Assert.True(
                        collected.StatusCode == System.Net.HttpStatusCode.OK,
                        "Checkpoint F-031 failed closed: HTTP " +
                        (int)collected.StatusCode + " " +
                        await collected.Content.ReadAsStringAsync());
                    var text = await collected.Content.ReadAsStringAsync();
                    Assert.DoesNotContain(ApiHarness.TargetToken, text);
                    Assert.DoesNotContain("other.invalid", text);
                    Assert.DoesNotContain("refs/heads/main", text);
                    using var json = JsonDocument.Parse(text);
                    var observed = json.RootElement;
                    observationId = observed.GetProperty("id").GetGuid();
                    targetReceiptSha = observed.GetProperty("evidenceSha256").GetString();
                    Assert.Equal(64, targetReceiptSha!.Length);
                    Assert.Equal(oidLength == 40 ? "sha1" : "sha256",
                        observed.GetProperty("objectFormat").GetString());
                    Assert.Equal(later.Id,
                        observed.GetProperty("checkpointId").GetGuid());
                    Assert.Equal(candidate.SourceSnapshotId,
                        observed.GetProperty("sourceSnapshotId").GetGuid());
                    Assert.Equal(2, observed.GetProperty("refCount").GetInt32());
                    foreach (var flag in new[] { "canObserveTarget",
                        "canPush", "canRetry", "canDispatch", "migrationComplete" })
                        Assert.False(observed.GetProperty(flag).GetBoolean());
                }
                else
                {
                    Assert.Equal(System.Net.HttpStatusCode.Conflict,
                        collected.StatusCode);
                    Assert.Contains("CheckpointTargetObservationPreflightUnverified",
                        await collected.Content.ReadAsStringAsync());
                }
            }
            var outboundAfterFirst = api.Calls.ToArray();
            Assert.Equal(decision ==
                GitCheckpointTargetHumanReviewStates.ApprovedForObservation
                ? 2 : 0, outboundAfterFirst.Length - outboundBefore);
            foreach (var call in outboundAfterFirst.Skip(outboundBefore))
            {
                Assert.Equal("GET", call.Method);
                Assert.Equal("https", call.Url.Scheme);
                Assert.Equal("api.github.com", call.Url.Host);
                Assert.Equal("Bearer", call.AuthScheme);
                Assert.StartsWith("/repos/gh-org/migration-one/git/matching-refs/",
                    call.Url.AbsolutePath);
            }
            if (observationId != Guid.Empty)
            {
                using (var replay = await SendAsync(
                    HttpMethod.Post, obsUrl, targetRequest))
                {
                    Assert.Equal(System.Net.HttpStatusCode.OK, replay.StatusCode);
                    using var json = JsonDocument.Parse(
                        await replay.Content.ReadAsStringAsync());
                    Assert.Equal(observationId,
                        json.RootElement.GetProperty("id").GetGuid());
                    Assert.Equal(targetReceiptSha,
                        json.RootElement.GetProperty("evidenceSha256").GetString());
                }
                using (var history = await SendAsync(
                    HttpMethod.Get, obsUrl + "?sourceRepositoryId=repo-1"))
                {
                    Assert.Equal(System.Net.HttpStatusCode.OK, history.StatusCode);
                    using var json = JsonDocument.Parse(
                        await history.Content.ReadAsStringAsync());
                    Assert.Equal(observationId,
                        json.RootElement.GetProperty("id").GetGuid());
                }
                using (var foreignReviewer = await SendAsync(
                    HttpMethod.Post, obsUrl, targetRequest, otherReviewerToken))
                {
                    Assert.Equal(System.Net.HttpStatusCode.Conflict,
                        foreignReviewer.StatusCode);
                }
                using (var foreignRepo = await SendAsync(
                    HttpMethod.Get, obsUrl + "?sourceRepositoryId=foreign"))
                    Assert.Equal(System.Net.HttpStatusCode.NotFound,
                        foreignRepo.StatusCode);
                // Concurrency/restart replays use the immutable receipt
                // and must not cause a second provider observation.
                var parallel = await Task.WhenAll(
                    Enumerable.Range(0, 3).Select(_ =>
                        SendAsync(HttpMethod.Post, obsUrl, targetRequest)));
                foreach (var same in parallel)
                {
                    using (same)
                    {
                        Assert.Equal(System.Net.HttpStatusCode.OK, same.StatusCode);
                        using var json = JsonDocument.Parse(
                            await same.Content.ReadAsStringAsync());
                        Assert.Equal(observationId,
                            json.RootElement.GetProperty("id").GetGuid());
                    }
                }
                Assert.Equal(outboundBefore + 2, api.Calls.Count);

                // #104 post-#109: join actual independently sealed F-091/
                // F-097b source and saved checkpoint F-031 into a VERSION 2
                // read-only comparison. Even after legitimate target GET,
                // original F-029 must remain stale and no workpack exists.
                await using (var verifiedDb = fixture.NewContext())
                {
                    var latestPlan = await new PostgresIncrementalOperationPlanRepository(
                        verifiedDb, clock).GetAsync(run.Id, plan.Id, default);
                    Assert.NotNull(latestPlan);
                    Assert.False(latestPlan.IsCurrent);
                    var fullRefs = await Ledger(verifiedDb, clock).GetVerifiedAsync(
                        run.Id, plan.Id, later.Id, "repo-1", default);
                    Assert.NotNull(fullRefs);
                    var savedTarget = await verifiedDb.GitCheckpointTargetObservations
                        .AsNoTracking().SingleAsync(x => x.Id == observationId);
                    var targetReceipt = JsonSerializer.Deserialize<
                        GitCheckpointTargetObservation>(savedTarget.ReceiptJson);
                    Assert.NotNull(targetReceipt);
                    // The live F-031 gate remains fully evaluated by the
                    // production service, but this long fixture no longer
                    // turns a valid source proof stale merely because
                    // hundreds of real Git/SQL assertions exceed 5 min.
                    // All tests that intentionally validate stale denial
                    // continue on TimeProvider.System.
                    Phase("F031 receipt acquired", targetReceipt.ObservedAtUtc);
                    longPhysicalClock?.BoundToOriginalObservation(
                        targetReceipt.ObservedAtUtc);
                    var joined = GitIncrementalCheckpointTargetJoin
                        .ForReadOnlyComparison(latestPlan, later, fullRefs,
                            targetReceipt);
                    Assert.Equal(2, joined.EvidenceVersion);

                    // A second, independent PostgreSQL preflight verifies
                    // the current reviewer, source chain, destination TTL,
                    // immutable F-031 audit and newest checkpoint. It is
                    // separately OFF-by-default, and cannot insert F-032.
                    await using var auditScope = api.Host.Services.CreateAsyncScope();
                    var reader = auditScope.ServiceProvider.GetRequiredService<
                        PostgresGitIncrementalLaterCheckpointAuditPreflight>();
                    var verified = await reader.ReadAsync(run.Id, plan.Id,
                        later.Id, observationId, "repo-1",
                        targetReceipt.EvidenceSha256, default);
                    Assert.Equal(joined.EvidenceVersion, verified.EvidenceVersion);
                    Assert.Equal(joined.TargetEvidenceSha256,
                        verified.TargetEvidenceSha256);
                    Assert.True(joined.ExpectedSourceRefs.SequenceEqual(
                        verified.ExpectedSourceRefs));
                    Assert.True(joined.ExpectedTargetRefs.SequenceEqual(
                        verified.ExpectedTargetRefs));
                    Assert.Equal("GitLaterCheckpointPreflightEvidenceChanged",
                        (await Assert.ThrowsAsync<
                            GitIncrementalCheckpointPreflightException>(() =>
                            reader.ReadAsync(run.Id, plan.Id, later.Id,
                                Guid.NewGuid(), "repo-1",
                                targetReceipt.EvidenceSha256,
                                default))).Code);
                    Assert.Equal("GitLaterCheckpointPreflightEvidenceChanged",
                        (await Assert.ThrowsAsync<
                            GitIncrementalCheckpointPreflightException>(() =>
                            reader.ReadAsync(run.Id, plan.Id, later.Id,
                                observationId, "repo-1",
                                new string('0', 64), default))).Code);
                    Assert.Equal("GitLaterCheckpointPreflightEvidenceChanged",
                        (await Assert.ThrowsAsync<
                            GitIncrementalCheckpointPreflightException>(() =>
                            reader.ReadAsync(run.Id, plan.Id, later.Id,
                                observationId, "foreign-repo",
                                targetReceipt.EvidenceSha256,
                                default))).Code);
                    Assert.Equal(fullRefs.EvidenceSha256,
                        joined.SourceFullRefProofSha256);
                    Assert.Equal(targetReceipt.EvidenceSha256,
                        joined.TargetEvidenceSha256);
                    Assert.False(joined.CanPush || joined.CanRetry ||
                        joined.CanDispatch || joined.MigrationComplete);
                    Assert.Empty(await verifiedDb.GitIncrementalCheckpointWorkpacks
                        .AsNoTracking().Where(x => x.CheckpointId == later.Id)
                        .ToArrayAsync());
                    Assert.Empty(await verifiedDb.IncrementalGitRefReconciliations
                        .AsNoTracking().Where(x => x.SnapshotId ==
                            candidate.SourceSnapshotId).ToArrayAsync());

                    // #104: a wholly separate, default-OFF source-locked
                    // F-032v2/F-038v2 persistence path. It must NEVER
                    // reach the production dispatch or legacy F-031 FK.
                    var writer = auditScope.ServiceProvider.GetRequiredService<
                        PostgresGitIncrementalLaterCheckpointWorkpackJournal>();
                    var first = await writer.CaptureAsync(
                        run.Id, plan.Id, later.Id, observationId, "repo-1",
                        targetReceipt.EvidenceSha256, candidate.RequestedBy,
                        default);
                    Assert.Equal(2, first.EvidenceVersion);
                    recordedWorkpackId = first.WorkpackId;
                    Assert.Equal("Blocked", first.State);
                    Assert.Equal("WriteBlocked", first.Phase);
                    Assert.Equal(targetReceipt.EvidenceSha256,
                        first.TargetReceiptSha256);
                    Assert.Equal(fullRefs.EvidenceSha256,
                        first.SourceFullRefProofSha256);
                    Assert.False(first.ReadyForExecution || first.CanPush ||
                        first.CanRetry || first.CanDispatch ||
                        first.MigrationComplete);
                    // #112: prepare the *next* source-only F-035v2
                    // request from verified immutable F-097b OIDs, never
                    // journal fingerprints, baseline F-029 refs, caller path
                    // or GitHub destination. This does not acquire a file.
                    var currentComparison = GitMirrorRefReconciler.Compare(new(
                        latestPlan.Id, latestPlan.ManifestSha256,
                        fullRefs.SourceEvidenceSha256,
                        joined.TargetEvidenceSha256,
                        joined.ExpectedSourceRefs, joined.ExpectedTargetRefs));
                    var bundlePreparation =
                        GitIncrementalLaterCheckpointBundleIntegrity.Prepare(
                            latestPlan, joined, fullRefs, currentComparison, first);
                    Assert.Equal(1, bundlePreparation.EvidenceVersion);
                    Assert.Equal(first.EvidenceSha256,
                        bundlePreparation.WorkpackEvidenceSha256);
                    Assert.Equal(targetReceipt.EvidenceSha256,
                        bundlePreparation.TargetReceiptSha256);
                    Assert.Equal(fullRefs.SourceManifestRootSha256,
                        bundlePreparation.SourceManifestRootSha256);
                    Assert.Equal(fullRefs.Refs,
                        bundlePreparation.SourceOnlyRequest.ExpectedRefs);
                    Assert.Equal(first.WorkpackId,
                        bundlePreparation.SourceOnlyRequest.WorkpackId);
                    Assert.False(bundlePreparation.CanAcquireBundle ||
                        bundlePreparation.CanPush || bundlePreparation.CanRetry ||
                        bundlePreparation.CanDispatch ||
                        bundlePreparation.MigrationComplete);
                    GitIncrementalLaterCheckpointBundleIntegrity.Verify(
                        bundlePreparation, latestPlan, joined, fullRefs,
                        currentComparison, first);
                    // A separate default-OFF PostgreSQL reader verifies
                    // both saved history and CURRENT approval before
                    // preparing the same never-authorizing F-035v2 plan.
                    var bundleReader = auditScope.ServiceProvider
                        .GetRequiredService<
                            PostgresGitIncrementalLaterCheckpointBundleReadPreflight>();
                    var persistedBundlePlan = await bundleReader.ReadAsync(
                        run.Id, plan.Id, later.Id, observationId,
                        first.WorkpackId, "repo-1",
                        targetReceipt.EvidenceSha256, default);
                    Assert.Equal(bundlePreparation.EvidenceSha256,
                        persistedBundlePlan.EvidenceSha256);
                    Assert.Equal(bundlePreparation.SourceOnlyRequest.ExpectedRefs,
                        persistedBundlePlan.SourceOnlyRequest.ExpectedRefs);
                    Assert.False(persistedBundlePlan.CanAcquireBundle ||
                        persistedBundlePlan.CanPush ||
                        persistedBundlePlan.CanRetry ||
                        persistedBundlePlan.CanDispatch ||
                        persistedBundlePlan.MigrationComplete);
                    Assert.Equal("LaterCheckpointBundleReadProvenanceUnverified",
                        (await Assert.ThrowsAsync<GitSourceBundleException>(() =>
                            bundleReader.ReadAsync(run.Id, plan.Id,
                                later.Id, observationId, first.WorkpackId,
                                "repo-1", new string('0', 64),
                                default))).Code);
                    Assert.Equal("LaterCheckpointBundleReadInvalidScope",
                        (await Assert.ThrowsAsync<GitSourceBundleException>(() =>
                            bundleReader.ReadAsync(run.Id, plan.Id,
                                later.Id, Guid.Empty, first.WorkpackId,
                                "repo-1", targetReceipt.EvidenceSha256,
                                default))).Code);

                    // #113: database lease claims are short transactions,
                    // serialised by source+plan locks. A competing scope
                    // cannot take the same F-038v2 workpack, and an
                    // old token cannot release the next generation.
                    var coordinator = auditScope.ServiceProvider
                        .GetRequiredService<
                            PostgresGitIncrementalLaterCheckpointBundleLeaseStore>();
                    var lease1 = await coordinator.ClaimAsync(
                        run.Id, plan.Id, later.Id, observationId,
                        first.WorkpackId, "repo-1",
                        targetReceipt.EvidenceSha256,
                        candidate.RequestedBy, TimeSpan.FromMinutes(2),
                        default);
                    Assert.Equal(1L, lease1.Generation);
                    Assert.False(lease1.CanAcquireBundle || lease1.CanPush ||
                        lease1.CanRetry || lease1.CanDispatch ||
                        lease1.MigrationComplete);
                    await using (var parallelScope =
                        api.Host.Services.CreateAsyncScope())
                    {
                        var competing = parallelScope.ServiceProvider
                            .GetRequiredService<
                                PostgresGitIncrementalLaterCheckpointBundleLeaseStore>();
                        Assert.Equal("LaterCheckpointBundleLeaseBusy",
                            (await Assert.ThrowsAsync<GitSourceBundleException>(
                                () => competing.ClaimAsync(run.Id, plan.Id,
                                    later.Id, observationId, first.WorkpackId,
                                    "repo-1", targetReceipt.EvidenceSha256,
                                    candidate.RequestedBy,
                                    TimeSpan.FromMinutes(2), default))).Code);
                    }
                    Assert.False(await coordinator.ReleaseAsync(
                        first.WorkpackId, Guid.NewGuid(), lease1.Generation,
                        candidate.RequestedBy, lease1.BundlePlanSha256,
                        default));
                    // Simulate failure AFTER a real trusted Git fsck
                    // and binary export but BEFORE durable SQL evidence.
                    // The file must survive byte-for-byte but move out
                    // of UUID.bundle upon independently approved recovery.
                    var orphanRunner = auditScope.ServiceProvider
                        .GetRequiredService<
                            GitIncrementalLaterCheckpointLocalBundleAcquisition>();
                    var orphan = await orphanRunner.RehearseAsync(
                        run.Id, plan.Id, later.Id, observationId,
                        first.WorkpackId, "repo-1",
                        targetReceipt.EvidenceSha256, default);
                    var orphanPath = Path.Combine(physicalMirror!.BundleRoot,
                        first.WorkpackId.ToString("N") + ".bundle");
                    Assert.True(File.Exists(orphanPath));
                    Assert.Empty(await verifiedDb.GitLaterCheckpointBundleEvidence
                        .AsNoTracking().Where(x => x.WorkpackId ==
                            first.WorkpackId).ToArrayAsync());
                    // #113: an incomplete or cancelled binary export is
                    // QUARANTINED, not silently available for reuse. Only
                    // the independently approved F-031 human reviewer can
                    // explicitly recover it with current source proof.
                    Assert.True(await coordinator.QuarantineAsync(
                        first.WorkpackId, lease1.LeaseToken,
                        lease1.Generation, candidate.RequestedBy,
                        lease1.BundlePlanSha256, default));
                    Assert.False(await coordinator.QuarantineAsync(
                        first.WorkpackId, lease1.LeaseToken,
                        lease1.Generation, candidate.RequestedBy,
                        lease1.BundlePlanSha256, default));
                    Assert.False(await coordinator.ReleaseAsync(
                        first.WorkpackId, lease1.LeaseToken,
                        lease1.Generation, candidate.RequestedBy,
                        lease1.BundlePlanSha256, default));
                    Assert.Equal("LaterCheckpointBundleLeaseRecoveryRequired",
                        (await Assert.ThrowsAsync<GitSourceBundleException>(
                            () => coordinator.ClaimAsync(run.Id, plan.Id,
                                later.Id, observationId, first.WorkpackId,
                                "repo-1", targetReceipt.EvidenceSha256,
                                candidate.RequestedBy,
                                TimeSpan.FromMinutes(2), default))).Code);
                    Assert.Equal("LaterCheckpointBundleRecoveryReviewerRequired",
                        (await Assert.ThrowsAsync<GitSourceBundleException>(
                            () => coordinator.RecoverAsync(run.Id, plan.Id,
                                later.Id, observationId, first.WorkpackId,
                                "repo-1", targetReceipt.EvidenceSha256,
                                candidate.RequestedBy, 1,
                                lease1.BundlePlanSha256, default))).Code);
                    Assert.True(await coordinator.RecoverAsync(
                        run.Id, plan.Id, later.Id, observationId,
                        first.WorkpackId, "repo-1",
                        targetReceipt.EvidenceSha256, reviewerId, 1,
                        lease1.BundlePlanSha256, default));
                    Assert.False(File.Exists(orphanPath));
                    var quarantinedFiles = Directory.GetFiles(
                        physicalMirror.BundleRoot,
                        first.WorkpackId.ToString("N") + ".*.quarantine");
                    Assert.Single(quarantinedFiles);
                    Assert.Equal(orphan.BundleBytes,
                        new FileInfo(quarantinedFiles[0]).Length);
                    await using (var preserved = File.OpenRead(quarantinedFiles[0]))
                        Assert.Equal(orphan.BundleSha256,
                            Convert.ToHexStringLower(
                                await SHA256.HashDataAsync(preserved)));
                    Assert.False(await coordinator.RecoverAsync(
                        run.Id, plan.Id, later.Id, observationId,
                        first.WorkpackId, "repo-1",
                        targetReceipt.EvidenceSha256, reviewerId, 1,
                        lease1.BundlePlanSha256, default));
                    var lease2 = await coordinator.ClaimAsync(
                        run.Id, plan.Id, later.Id, observationId,
                        first.WorkpackId, "repo-1",
                        targetReceipt.EvidenceSha256,
                        candidate.RequestedBy, TimeSpan.FromMinutes(2),
                        default);
                    Assert.Equal(3L, lease2.Generation);
                    Assert.NotEqual(lease1.LeaseToken, lease2.LeaseToken);
                    Assert.False(await coordinator.ReleaseAsync(
                        first.WorkpackId, lease1.LeaseToken,
                        lease1.Generation, candidate.RequestedBy,
                        lease1.BundlePlanSha256, default));
                    var leaseEvents = await verifiedDb
                        .GitLaterCheckpointBundleLeaseEvents.AsNoTracking()
                        .Where(x => x.WorkpackId == first.WorkpackId)
                        .OrderBy(x => x.Id).ToArrayAsync();
                    Assert.Equal(new[] {
                        "Acquired", "Quarantined", "Recovered", "Acquired" },
                        leaseEvents.Select(x => x.Type));
                    Assert.Equal(new long[] { 1, 1, 2, 3 },
                        leaseEvents.Select(x => x.Generation));
                    Assert.Equal(reviewerId, leaseEvents[2].ActorId);
                    Assert.All(leaseEvents, ev =>
                        Assert.Equal(bundlePreparation.EvidenceSha256,
                            ev.BundlePlanSha256));
                    Assert.Empty(await verifiedDb.GitLaterCheckpointBundleEvidence
                        .AsNoTracking().Where(x => x.WorkpackId ==
                            first.WorkpackId).ToArrayAsync());

                    // A replica crashing past expiry is quarantined by
                    // the next claim, not silently replaced. Force the
                    // persisted lease into the past with SQL constraints.
                    await verifiedDb.Database.ExecuteSqlInterpolatedAsync($"""
                        UPDATE git_later_checkpoint_bundle_leases
                        SET acquired_at_utc = now() - interval '4 minutes',
                            lease_until_utc = now() - interval '1 minute',
                            updated_at_utc = now()
                        WHERE workpack_id = {first.WorkpackId}
                            AND generation = {lease2.Generation}
                            AND lease_token = {lease2.LeaseToken}
                        """);
                    Assert.Equal("LaterCheckpointBundleLeaseRecoveryRequired",
                        (await Assert.ThrowsAsync<GitSourceBundleException>(
                            () => coordinator.ClaimAsync(run.Id, plan.Id,
                                later.Id, observationId, first.WorkpackId,
                                "repo-1", targetReceipt.EvidenceSha256,
                                candidate.RequestedBy, TimeSpan.FromMinutes(2),
                                default))).Code);
                    Assert.False(await coordinator.ReleaseAsync(
                        first.WorkpackId, lease2.LeaseToken,
                        lease2.Generation, candidate.RequestedBy,
                        lease2.BundlePlanSha256, default));
                    Assert.True(await coordinator.RecoverAsync(
                        run.Id, plan.Id, later.Id, observationId,
                        first.WorkpackId, "repo-1",
                        targetReceipt.EvidenceSha256, reviewerId,
                        lease2.Generation, lease2.BundlePlanSha256, default));
                    // Three independent database scopes race to claim
                    // the recovered lease (no in-process shared lock).
                    // Source-first PostgreSQL advisory locks must admit
                    // EXACTLY one generation-5 claimant.
                    var contenders = await Task.WhenAll(
                        Enumerable.Range(0, 3).Select(async _ =>
                        {
                            await using var contenderScope =
                                api.Host.Services.CreateAsyncScope();
                            var competingStore = contenderScope.ServiceProvider
                                .GetRequiredService<
                                    PostgresGitIncrementalLaterCheckpointBundleLeaseStore>();
                            try
                            {
                                var claim = await competingStore.ClaimAsync(
                                    run.Id, plan.Id, later.Id, observationId,
                                    first.WorkpackId, "repo-1",
                                    targetReceipt.EvidenceSha256,
                                    candidate.RequestedBy,
                                    TimeSpan.FromMinutes(2), default);
                                return (Claim: claim, Code: (string?)null);
                            }
                            catch (GitSourceBundleException ex)
                            {
                                return (Claim:
                                    (GitIncrementalLaterCheckpointBundleLeaseClaim?)null,
                                    Code: ex.Code);
                            }
                        }));
                    Assert.Single(contenders.Where(x => x.Claim is not null));
                    Assert.Equal(2, contenders.Count(x =>
                        x.Code == "LaterCheckpointBundleLeaseBusy"));
                    var lease3 = contenders.Single(x => x.Claim is not null)
                        .Claim!;
                    Assert.Equal(5L, lease3.Generation);
                    Assert.NotEqual(lease2.LeaseToken, lease3.LeaseToken);
                    Assert.False(await coordinator.ReleaseAsync(
                        first.WorkpackId, lease2.LeaseToken,
                        lease2.Generation, candidate.RequestedBy,
                        lease2.BundlePlanSha256, default));
                    var leaseEventsAfterExpiry = await verifiedDb
                        .GitLaterCheckpointBundleLeaseEvents.AsNoTracking()
                        .Where(x => x.WorkpackId == first.WorkpackId)
                        .OrderBy(x => x.Id).ToArrayAsync();
                    Assert.Equal(new[] {
                        "Acquired", "Quarantined", "Recovered",
                        "Acquired", "Quarantined", "Recovered", "Acquired"
                    }, leaseEventsAfterExpiry.Select(x => x.Type));
                    Assert.Equal(new long[] { 1, 1, 2, 3, 3, 4, 5 },
                        leaseEventsAfterExpiry.Select(x => x.Generation));
                    Assert.Equal(reviewerId, leaseEventsAfterExpiry[5].ActorId);

                    // #113: even with a valid current lease, a forged
                    // result without actual on-disk bundle bytes cannot
                    // become durable F-035v2 SQL evidence. The trusted
                    // filesystem verifier fails BEFORE Git or any INSERT.
                    var emptyBundleRoot = Path.Combine(Path.GetTempPath(),
                        "f035v2-missing-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(emptyBundleRoot);
                    try
                    {
                        var checkedAt = DateTimeOffset.UtcNow;
                        var fakeFile = new
                            GitIncrementalLaterCheckpointLocalBundleResult(
                                first.WorkpackId, later.Id,
                                bundlePreparation.SourceSnapshotId,
                                bundlePreparation.EvidenceSha256,
                                bundlePreparation.SourceFullRefProofSha256,
                                targetReceipt.EvidenceSha256,
                                first.WorkpackId.ToString("N") + ".bundle",
                                new string('a', 64), 123, fullRefs.Refs.Count,
                                checkedAt.AddSeconds(-1), checkedAt,
                                false, false, false, false, false);
                        var absent = await Assert.ThrowsAsync<
                            GitSourceBundleException>(() =>
                            coordinator.CommitAsync(lease3, fakeFile,
                                run.Id, plan.Id, later.Id,
                                observationId, "repo-1",
                                targetReceipt.EvidenceSha256,
                                new LocalGitBundleArtifactStore(emptyBundleRoot),
                                default));
                        Assert.Equal("BundleStoredFileMissing", absent.Code);
                        Assert.Empty(await verifiedDb.GitLaterCheckpointBundleEvidence
                            .AsNoTracking().Where(x => x.WorkpackId ==
                                first.WorkpackId).ToArrayAsync());
                        Assert.Empty(await verifiedDb.GitLaterCheckpointBundleEvents
                            .AsNoTracking().Where(x => x.WorkpackId ==
                                first.WorkpackId).ToArrayAsync());
                    }
                    finally
                    {
                        Directory.Delete(emptyBundleRoot, recursive: true);
                    }
                    // The immutable lease audit itself must reject UPDATE
                    // independently of any application-side guard.
                    var changeAudit = await Assert.ThrowsAsync<
                        Npgsql.PostgresException>(() =>
                        verifiedDb.Database.ExecuteSqlInterpolatedAsync($"""
                            DELETE FROM git_later_checkpoint_bundle_lease_events
                            WHERE workpack_id = {first.WorkpackId}
                            """));
                    Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException,
                        changeAudit.SqlState);
                    var enableRemote = await Assert.ThrowsAsync<
                        Npgsql.PostgresException>(() =>
                        verifiedDb.Database.ExecuteSqlInterpolatedAsync($"""
                            UPDATE git_later_checkpoint_bundle_leases
                            SET can_push = {true}
                            WHERE workpack_id = {first.WorkpackId}
                            """));
                    Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation,
                        enableRemote.SqlState);

                    var replay = await writer.CaptureAsync(
                        run.Id, plan.Id, later.Id, observationId, "repo-1",
                        targetReceipt.EvidenceSha256, candidate.RequestedBy,
                        default);
                    Assert.Equal(first, replay);

                    // #111 immutable history never serializes provider refs,
                    // OIDs, stored actor/reviewer UUIDs or write capability.
                    var historyUrl =
                        "/api/incremental-preparations/" + run.Id +
                        "/operation-plans/" + plan.Id +
                        "/source-sync-checkpoints/" + later.Id +
                        "/later-workpacks/" + first.WorkpackId +
                        "/history?sourceRepositoryId=repo-1" +
                        "&targetObservationId=" + observationId +
                        "&expectedTargetReceiptSha256=" +
                        targetReceipt.EvidenceSha256;
                    var historyCalls = api.Calls.Count;
                    using (var history = await SendAsync(
                        HttpMethod.Get, historyUrl))
                    {
                        Assert.Equal(System.Net.HttpStatusCode.OK,
                            history.StatusCode);
                        using var data = JsonDocument.Parse(
                            await history.Content.ReadAsStringAsync());
                        var result = data.RootElement;
                        Assert.Equal(first.WorkpackId,
                            result.GetProperty("workpackId").GetGuid());
                        Assert.Equal(first.EvidenceSha256,
                            result.GetProperty("evidenceSha256").GetString());
                        Assert.Equal(first.TargetReceiptSha256,
                            result.GetProperty("targetReceiptSha256").GetString());
                        Assert.Equal("Blocked",
                            result.GetProperty("state").GetString());
                        Assert.Equal("WriteBlocked",
                            result.GetProperty("phase").GetString());
                        Assert.True(result.GetProperty("historicalOnly")
                            .GetBoolean());
                        foreach (var flag in new[] {
                            "currentApprovalVerified", "readyForExecution",
                            "canPush", "canRetry", "canDispatch",
                            "migrationComplete"
                        })
                            Assert.False(result.GetProperty(flag).GetBoolean());
                        Assert.Equal(2, result.GetProperty("sourceRefCount")
                            .GetInt32());
                        var jsonText = result.GetRawText();
                        foreach (var forbidden in new[] {
                            "refs/heads/", "refs/tags/", "objectId",
                            "itemsJson", "receiptJson", "recordedBy",
                            "reviewedBy", "requestedBy",
                            candidate.RequestedBy.ToString("D"),
                            reviewerId.ToString("D"), ApiHarness.TargetToken
                        })
                            Assert.DoesNotContain(forbidden, jsonText,
                                StringComparison.OrdinalIgnoreCase);
                    }
                    Assert.Equal(historyCalls, api.Calls.Count);
                    using (var wrongScope = await SendAsync(
                        HttpMethod.Get, historyUrl.Replace(
                            "targetObservationId=" + observationId,
                            "targetObservationId=" + Guid.NewGuid())))
                        Assert.Equal(System.Net.HttpStatusCode.NotFound,
                            wrongScope.StatusCode);
                    using (var wrongDigest = await SendAsync(
                        HttpMethod.Get, historyUrl.Replace(
                            targetReceipt.EvidenceSha256,
                            new string('0', 64))))
                        Assert.Equal(System.Net.HttpStatusCode.Conflict,
                            wrongDigest.StatusCode);
                    // Restart-like independent DI contexts and concurrent
                    // requests must all replay the same immutable witness;
                    // the shared source advisory lock prevents double INSERT.
                    var concurrent = await Task.WhenAll(
                        Enumerable.Range(0, 3).Select(async _ =>
                        {
                            await using var parallelScope =
                                api.Host.Services.CreateAsyncScope();
                            var independentWriter = parallelScope.ServiceProvider
                                .GetRequiredService<
                                    PostgresGitIncrementalLaterCheckpointWorkpackJournal>();
                            return await independentWriter.CaptureAsync(
                                run.Id, plan.Id, later.Id, observationId,
                                "repo-1", targetReceipt.EvidenceSha256,
                                candidate.RequestedBy, default);
                        }));
                    Assert.All(concurrent, saved => Assert.Equal(first, saved));
                    Assert.Equal("LaterCheckpointWorkpackContextChanged",
                        (await Assert.ThrowsAsync<
                            GitIncrementalCheckpointWorkpackException>(() =>
                            writer.CaptureAsync(run.Id, plan.Id, later.Id,
                                observationId, "repo-1",
                                targetReceipt.EvidenceSha256, Guid.NewGuid(),
                                default))).Code);
                    Assert.Equal("LaterCheckpointWorkpackPreflightUnverified",
                        (await Assert.ThrowsAsync<
                            GitIncrementalCheckpointWorkpackException>(() =>
                            writer.CaptureAsync(run.Id, plan.Id, later.Id,
                                observationId, "repo-1", new string('0', 64),
                                candidate.RequestedBy, default))).Code);

                    foreach (var wrong in new[] {
                        (CheckpointId: Guid.NewGuid(), RepositoryId: "repo-1"),
                        (CheckpointId: later.Id, RepositoryId: "foreign-repo")
                    })
                        Assert.Equal("LaterCheckpointWorkpackPreflightUnverified",
                            (await Assert.ThrowsAsync<
                                GitIncrementalCheckpointWorkpackException>(() =>
                                writer.CaptureAsync(run.Id, plan.Id,
                                    wrong.CheckpointId, observationId,
                                    wrong.RepositoryId,
                                    targetReceipt.EvidenceSha256,
                                    candidate.RequestedBy, default))).Code);

                    var recon = await verifiedDb.GitLaterCheckpointReconciliations
                        .AsNoTracking().SingleAsync(x => x.Id ==
                            first.ReconciliationId);
                    var pack = await verifiedDb.GitLaterCheckpointWorkpacks
                        .AsNoTracking().SingleAsync(x => x.Id == first.WorkpackId);
                    var events = await verifiedDb.GitLaterCheckpointWorkpackEvents
                        .AsNoTracking().Where(x => x.WorkpackId == first.WorkpackId)
                        .ToArrayAsync();
                    Assert.Single(events);
                    Assert.Equal(first.EvidenceSha256, pack.EvidenceSha256);
                    Assert.Equal(first.CheckpointTargetObservationId,
                        recon.TargetObservationId);
                    Assert.Equal(first.CheckpointTargetObservationId,
                        pack.TargetObservationId);
                    Assert.Equal("Blocked", pack.State);
                    Assert.Equal("WriteBlocked", pack.Phase);
                    Assert.False(recon.ReadyForExecution ||
                        pack.ReadyForExecution || pack.CanPush ||
                        pack.CanRetry || pack.CanDispatch ||
                        pack.MigrationComplete);

                    // #112: the new F-035v2 table is isolated from
                    // legacy F-035, references a v2 F-038 workpack and
                    // independently rejects executable evidence even
                    // when inserted via direct SQL without any API.
                    Assert.Empty(await verifiedDb.GitLaterCheckpointBundleEvidence
                        .AsNoTracking().Where(x => x.WorkpackId ==
                            first.WorkpackId).ToArrayAsync());
                    var v2ForeignKeys = await verifiedDb.Database
                        .SqlQueryRaw<long>("""
                            SELECT count(*)::bigint AS "Value"
                            FROM pg_constraint WHERE contype = 'f'
                            AND conrelid =
                                'git_later_checkpoint_bundle_evidence'::regclass
                            AND confrelid =
                                'git_later_checkpoint_workpacks'::regclass
                            """).SingleAsync();
                    var legacyForeignKeys = await verifiedDb.Database
                        .SqlQueryRaw<long>("""
                            SELECT count(*)::bigint AS "Value"
                            FROM pg_constraint WHERE contype = 'f'
                            AND conrelid =
                                'git_later_checkpoint_bundle_evidence'::regclass
                            AND confrelid =
                                'git_transfer_workpacks'::regclass
                            """).SingleAsync();
                    Assert.Equal(1L, v2ForeignKeys);
                    Assert.Equal(0L, legacyForeignKeys);
                    var f035Triggers = await verifiedDb.Database
                        .SqlQueryRaw<long>("""
                            SELECT count(*)::bigint AS "Value" FROM pg_trigger
                            WHERE tgrelid =
                                'git_later_checkpoint_bundle_evidence'::regclass
                                AND tgname='trg_later_checkpoint_f035_immutable'
                                AND NOT tgisinternal
                            """).SingleAsync();
                    Assert.Equal(1L, f035Triggers);
                    var timestamp = DateTimeOffset.UtcNow;
                    var invalidF035 = await Assert.ThrowsAsync<
                        Npgsql.PostgresException>(() =>
                        verifiedDb.Database.ExecuteSqlInterpolatedAsync($"""
                            INSERT INTO git_later_checkpoint_bundle_evidence (
                                workpack_id, preparation_id, plan_id,
                                checkpoint_id, source_snapshot_id,
                                target_observation_id, source_repository_id,
                                workpack_evidence_sha256, bundle_plan_sha256,
                                full_ref_proof_sha256, source_evidence_sha256,
                                source_manifest_root_sha256,
                                target_receipt_sha256, reconciliation_sha256,
                                bundle_file_name, bundle_sha256,
                                bundle_bytes, ref_count, acquired_at_utc,
                                recorded_at_utc, recorded_by, evidence_sha256,
                                ready_for_execution, can_push, can_retry,
                                can_dispatch, migration_complete)
                            VALUES (
                                {first.WorkpackId}, {run.Id}, {plan.Id},
                                {later.Id}, {fullRefs.SnapshotId},
                                {observationId}, {"repo-1"},
                                {first.EvidenceSha256},
                                {bundlePreparation.EvidenceSha256},
                                {fullRefs.EvidenceSha256},
                                {fullRefs.SourceEvidenceSha256},
                                {fullRefs.SourceManifestRootSha256},
                                {targetReceipt.EvidenceSha256},
                                {first.ReconciliationSha256},
                                {first.WorkpackId.ToString("N") + ".bundle"},
                                {new string('a', 64)}, {128L}, {2},
                                {timestamp}, {timestamp},
                                {candidate.RequestedBy}, {new string('b', 64)},
                                {false}, {true}, {false}, {false}, {false})
                            """));
                    Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation,
                        invalidF035.SqlState);
                    Assert.Empty(await verifiedDb.GitLaterCheckpointBundleEvidence
                        .AsNoTracking().Where(x => x.WorkpackId ==
                            first.WorkpackId).ToArrayAsync());

                    // Both SQL layers independently forbid mutation, even
                    // with no API/worker involved or an existing EF table.
                    var update = await Assert.ThrowsAsync<Npgsql.PostgresException>(
                        () => verifiedDb.Database.ExecuteSqlInterpolatedAsync($"""
                            UPDATE git_later_checkpoint_workpacks
                            SET can_push = {true}
                            WHERE id = {first.WorkpackId}
                            """));
                    Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException,
                        update.SqlState);
                    var erase = await Assert.ThrowsAsync<Npgsql.PostgresException>(
                        () => verifiedDb.Database.ExecuteSqlInterpolatedAsync($"""
                            DELETE FROM git_later_checkpoint_reconciliations
                            WHERE id = {first.ReconciliationId}
                            """));
                    Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException,
                        erase.SqlState);
                    Assert.Empty(await verifiedDb.GitIncrementalCheckpointWorkpacks
                        .AsNoTracking().Where(x => x.CheckpointId == later.Id)
                        .ToArrayAsync());
                    Assert.Empty(await verifiedDb.IncrementalGitRefReconciliations
                        .AsNoTracking().Where(x => x.SnapshotId ==
                            candidate.SourceSnapshotId).ToArrayAsync());
                    // #113: FULL F-091/F-097b -> reviewed F-031 ->
                    // sealed F-038v2 -> REAL local SHA1/SHA256 Git bare
                    // mirror -> fsck -> UUID.bundle -> same-source lease
                    // fencing -> independent on-disk rehash -> atomic
                    // F-035v2 record + immutable audit (no remote writes).
                    var offline = auditScope.ServiceProvider.GetRequiredService<
                        GitIncrementalLaterCheckpointLocalBundleAcquisition>();
                    var rehearsed = await offline.RehearseAsync(
                        run.Id, plan.Id, later.Id, observationId,
                        first.WorkpackId, "repo-1",
                        targetReceipt.EvidenceSha256, default);
                    Assert.True(rehearsed.BundleBytes > 0);
                    Assert.False(rehearsed.DurableEvidencePersisted ||
                        rehearsed.CanPush || rehearsed.CanRetry ||
                        rehearsed.CanDispatch || rehearsed.MigrationComplete);
                    var verifier = new LocalGitBundleArtifactStore(
                        physicalMirror!.BundleRoot);
                    var committed = await coordinator.CommitAsync(
                        lease3, rehearsed, run.Id, plan.Id, later.Id,
                        observationId, "repo-1",
                        targetReceipt.EvidenceSha256, verifier, default);
                    Assert.Equal(1, committed.EvidenceVersion);
                    Assert.Equal(first.WorkpackId, committed.WorkpackId);
                    Assert.Equal(bundlePreparation.EvidenceSha256,
                        committed.BundlePlanSha256);
                    Assert.Equal(first.EvidenceSha256,
                        committed.WorkpackEvidenceSha256);
                    Assert.Equal(rehearsed.BundleSha256,
                        committed.BundleSha256);
                    Assert.Equal(rehearsed.BundleBytes,
                        committed.BundleBytes);
                    Assert.False(committed.ReadyForExecution ||
                        committed.CanPush || committed.CanRetry ||
                        committed.CanDispatch || committed.MigrationComplete);
                    GitIncrementalLaterCheckpointBundleReceiptIntegrity.Verify(
                        committed, bundlePreparation, rehearsed);
                    // #100: F-060v2 currently creates a NON-authorizing
                    // physical-mirror staging witness from actual sealed
                    // PostgreSQL F-035v2 and F-097b source OIDs. It is
                    // explicitly NOT the legacy F-060 importer plan:
                    // F-029 IsCurrent remains false for the later epoch.
                    var staging = GitIncrementalLaterCheckpointMirrorStagingIntegrity
                        .Prepare(latestPlan, joined, fullRefs,
                            bundlePreparation, committed, first);
                    Assert.Equal(1, staging.EvidenceVersion);
                    Assert.Equal(0L, staging.F061LeaseGeneration);
                    Assert.Equal(later.Id, staging.CheckpointId);
                    Assert.Equal(fullRefs.SnapshotId, staging.SourceSnapshotId);
                    Assert.NotEqual(plan.SnapshotId, staging.SourceSnapshotId);
                    Assert.Equal(first.WorkpackId, staging.WorkpackId);
                    Assert.Equal(
                        GitSourceSnapshotMirrorIntegrity.PhysicalMirrorId(
                            fullRefs.SnapshotId, first.WorkpackId),
                        staging.PhysicalMirrorId);
                    Assert.NotEqual(
                        GitSourceSnapshotMirrorIntegrity.PhysicalMirrorId(
                            plan.SnapshotId, first.WorkpackId),
                        staging.PhysicalMirrorId);
                    Assert.Equal(fullRefs.Refs, staging.SourceRefs);
                    Assert.Equal(fullRefs.EvidenceSha256,
                        staging.SourceFullRefProofSha256);
                    Assert.Equal(committed.EvidenceSha256,
                        staging.BundleEvidenceSha256);
                    Assert.Equal(committed.BundleSha256, staging.BundleSha256);
                    Assert.Equal(targetReceipt.EvidenceSha256,
                        staging.TargetReceiptSha256);
                    Assert.Equal(oidLength == 40 ? "sha1" : "sha256",
                        staging.GitObjectFormat);
                    Assert.False(staging.CanMaterialize || staging.CanPush ||
                        staging.CanRetry || staging.CanDispatch ||
                        staging.MigrationComplete);
                    GitIncrementalLaterCheckpointMirrorStagingIntegrity.Verify(
                        staging, latestPlan, joined, fullRefs,
                        bundlePreparation, committed, first);
                    // Separate default-OFF PostgreSQL reader: live human
                    // approval and independently verified F-038v2 plus
                    // the committed F-035v2 with actual byte SHA readback.
                    var mirrorReader = auditScope.ServiceProvider
                        .GetRequiredService<
                            PostgresGitIncrementalLaterCheckpointMirrorStagingPreflight>();
                    var readStaging = await mirrorReader.ReadAsync(
                        run.Id, plan.Id, later.Id, observationId,
                        first.WorkpackId, "repo-1",
                        targetReceipt.EvidenceSha256,
                        candidate.RequestedBy, default);
                    Assert.Equal(staging.EvidenceSha256, readStaging.EvidenceSha256);
                    Assert.Equal(staging.PhysicalMirrorId,
                        readStaging.PhysicalMirrorId);
                    Assert.Equal(fullRefs.Refs, readStaging.SourceRefs);
                    Assert.False(readStaging.CanMaterialize ||
                        readStaging.CanPush || readStaging.CanRetry ||
                        readStaging.CanDispatch || readStaging.MigrationComplete);
                    // #117: same REAL later-epoch PostgreSQL source and
                    // full F-097b/F-031/F-038v2/F-035v2 are rechecked,
                    // including exact immutable SQL rows under source-first
                    // advisory lock. Still no lease, Git or target writes.
                    var admission = auditScope.ServiceProvider
                        .GetRequiredService<
                            PostgresGitIncrementalLaterCheckpointMirrorAdmissionPreflight>();
                    var checkedAdmission = await admission.ReadAsync(
                        run.Id, plan.Id, later.Id, observationId,
                        first.WorkpackId, "repo-1",
                        targetReceipt.EvidenceSha256,
                        candidate.RequestedBy, default);
                    Assert.Equal(readStaging.EvidenceSha256,
                        checkedAdmission.StagingEvidenceSha256);
                    Assert.Equal(readStaging.PhysicalMirrorId,
                        checkedAdmission.PhysicalMirrorId);
                    Assert.Equal(fullRefs.SnapshotId,
                        checkedAdmission.SourceSnapshotId);
                    Assert.Equal(committed.BundleSha256,
                        checkedAdmission.BundleSha256);
                    Assert.False(checkedAdmission.DurableLeasePersisted ||
                        checkedAdmission.CanMaterialize ||
                        checkedAdmission.CanPush ||
                        checkedAdmission.CanRetry ||
                        checkedAdmission.CanDispatch ||
                        checkedAdmission.MigrationComplete);
                    Assert.Equal(0, checkedAdmission.F061LeaseGeneration);
                    GitIncrementalLaterCheckpointMirrorAdmissionIntegrity.Verify(
                        checkedAdmission, readStaging, committed);
                    Assert.Empty(await verifiedDb.GitSourceMirrorLifecycles
                        .AsNoTracking().Where(x =>
                            x.MirrorId == checkedAdmission.PhysicalMirrorId)
                        .ToArrayAsync());
                    Assert.False(checkedAdmission.DurableLeasePersisted);
                    Assert.Equal(0L, checkedAdmission.F061LeaseGeneration);
                    Phase("bundle/mirror admission done", targetReceipt.ObservedAtUtc);
                    if (runWorker)
                    {
                        // #120: end-to-end independently default-OFF
                        // source-fenced acquisition, periodic SQL heartbeat,
                        // exact UUID.bundle -> UUID.git import, double
                        // physical fsck and immutable F-061v2 Verified.
                        var blockingImporter = new CancellableMirrorImporter();
                        IGitIncrementalLaterCheckpointMirrorImporter chosenImporter =
                            fenceWorker
                                ? blockingImporter
                                : new DelayedVerifiedImporter(
                                    auditScope.ServiceProvider
                                        .GetRequiredService<
                                            IGitIncrementalLaterCheckpointMirrorImporter>(),
                                    failWorker, failAfterImport,
                                 async (mirrorId, heartbeatCt) =>
                                 {
                                     if (failWorker || failAfterImport)
                                     {
                                         await Task.Delay(
                                             TimeSpan.FromSeconds(3),
                                             heartbeatCt);
                                         return;
                                     }
                                     // Wait on independent PostgreSQL read
                                     // contexts for two completed lease
                                     // renewals while Git import is blocked.
                                     // The final post-import renewal does
                                     // not count toward this requirement.
                                     using var deadline = CancellationTokenSource
                                         .CreateLinkedTokenSource(heartbeatCt);
                                     deadline.CancelAfter(
                                         TimeSpan.FromSeconds(90));
                                     while (true)
                                     {
                                         await using var pulseDb =
                                             fixture.NewContext();
                                         var renewals = await pulseDb.Database
                                             .SqlQuery<long>($"""
                                                 SELECT count(*)::bigint AS "Value"
                                                 FROM git_later_checkpoint_mirror_lease_events
                                                 WHERE mirror_id = {mirrorId}
                                                     AND type = 'Renewed'
                                                 """).SingleAsync(deadline.Token);
                                         if (renewals >= 2)
                                             break;
                                         await Task.Delay(
                                             TimeSpan.FromMilliseconds(200),
                                             deadline.Token);
                                     }
                                 });
                        var mirrorWorkerRunner = new
                            GitIncrementalLaterCheckpointMirrorImportWorker(
                                api.Host.Services.GetRequiredService<
                                    IServiceScopeFactory>(),
                                new(
                                    true, TimeSpan.FromMinutes(3),
                                    TimeSpan.FromMilliseconds(250)),
                                chosenImporter);
                        var activeWorker = Guid.NewGuid();
                        if (fenceWorker)
                        {
                            // One independent replica quarantines the real
                            // Held generation while an import is blocked.
                            // The next source-bound heartbeat must fence
                            // the worker, CANCEL pending local Git and
                            // forbid any immutable Verified event.
                            var pending = mirrorWorkerRunner.RunAsync(
                                run.Id, plan.Id, later.Id, observationId,
                                first.WorkpackId, "repo-1",
                                targetReceipt.EvidenceSha256,
                                candidate.RequestedBy, activeWorker,
                                default);
                            await blockingImporter.Entered.WaitAsync(
                                TimeSpan.FromSeconds(90));
                            await using var fenceScope =
                                api.Host.Services.CreateAsyncScope();
                            var fencingStore = fenceScope.ServiceProvider
                                .GetRequiredService<
                                    PostgresGitIncrementalLaterCheckpointMirrorLeaseStore>();
                            var tokenInDatabase = await verifiedDb.Database
                                .SqlQuery<Guid>($"""
                                    SELECT lease_token AS "Value"
                                    FROM git_later_checkpoint_mirror_leases
                                    WHERE mirror_id = {staging.PhysicalMirrorId}
                                    """).SingleAsync();
                            var quarantined = false;
                            for (var attempt = 0;
                                attempt < 12 && !quarantined; attempt++)
                            {
                                var currentDeadline = await verifiedDb.Database
                                    .SqlQuery<DateTimeOffset>($"""
                                        SELECT lease_until_utc AS "Value"
                                        FROM git_later_checkpoint_mirror_leases
                                        WHERE mirror_id = {staging.PhysicalMirrorId}
                                        """).SingleAsync();
                                var handle =
                                    new GitIncrementalLaterCheckpointMirrorLeaseClaim(
                                        staging.PhysicalMirrorId,
                                        first.WorkpackId,
                                        staging.SourceSnapshotId,
                                        later.Id, tokenInDatabase, 1,
                                        activeWorker, candidate.RequestedBy,
                                        staging.EvidenceSha256,
                                        currentDeadline, false, false, false,
                                        false, false);
                                quarantined = await fencingStore
                                    .QuarantineAsync(handle,
                                        "TestOwnerRevoked", default);
                            }
                            Assert.True(quarantined);
                            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                                () => pending.WaitAsync(
                                    TimeSpan.FromSeconds(90)));
                            var fencedState = await verifiedDb.Database
                                .SqlQuery<string>($"""
                                    SELECT state AS "Value"
                                    FROM git_later_checkpoint_mirror_leases
                                    WHERE mirror_id = {staging.PhysicalMirrorId}
                                    """).SingleAsync();
                            Assert.Equal("RecoveryRequired", fencedState);
                            var fencedEvents = await verifiedDb.Database
                                .SqlQuery<string>($"""
                                    SELECT type AS "Value"
                                    FROM git_later_checkpoint_mirror_lease_events
                                    WHERE mirror_id = {staging.PhysicalMirrorId}
                                    """).ToArrayAsync();
                            Assert.Contains("Acquired", fencedEvents);
                            Assert.Equal(1, fencedEvents.Count(x =>
                                x == "Quarantined"));
                            Assert.DoesNotContain("Verified", fencedEvents);
                            var noneVerified = await verifiedDb.Database
                                .SqlQuery<long>($"""
                                    SELECT count(*)::bigint AS "Value"
                                    FROM git_later_checkpoint_mirror_verified_evidence
                                    WHERE mirror_id = {staging.PhysicalMirrorId}
                                    """).SingleAsync();
                            Assert.Equal(0L, noneVerified);
                            var recoveryInspector = auditScope.ServiceProvider
                                .GetRequiredService<
                                    PostgresGitIncrementalLaterCheckpointMirrorRecoveryInspector>();
                            var diagnostic = await recoveryInspector.ReadAsync(
                                run.Id, plan.Id, later.Id, observationId,
                                first.WorkpackId, "repo-1",
                                targetReceipt.EvidenceSha256,
                                candidate.RequestedBy, default);
                            Assert.Equal(staging.PhysicalMirrorId,
                                diagnostic.PhysicalMirrorId);
                            Assert.Equal("TestOwnerRevoked", diagnostic.FailureCode);
                            Assert.Equal(activeWorker, diagnostic.WorkerId);
                            Assert.Equal(1L, diagnostic.LeaseGeneration);
                            Assert.Equal(
                                GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity
                                    .Uninspectable, diagnostic.PhysicalState);
                            Assert.Null(diagnostic.ObjectSetSha256);
                            Assert.Null(diagnostic.ObjectCount);
                            Assert.Equal(fencedEvents.Length,
                                diagnostic.LeaseEventCount);
                            Assert.Equal(fencedEvents.Count(x => x == "Renewed"),
                                diagnostic.RenewedEventCount);
                            Assert.False(diagnostic.DurableRecoveryApprovalPersisted ||
                                diagnostic.CanReAdmit || diagnostic.CanMaterialize ||
                                diagnostic.CanPush || diagnostic.CanRetry ||
                                diagnostic.CanDispatch || diagnostic.MigrationComplete);
                            GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity
                                .Verify(diagnostic, staging, committed, null);
                        }
                        else if (failWorker || failAfterImport)
                        {
                            // Deliberately interrupted import after
                            // several live heartbeats: verify the exact
                            // owner becomes RecoveryRequired and NO
                            // physical Verified proof is ever persisted.
                            var thrown = await Assert.ThrowsAsync<
                                GitSourceSnapshotMirrorException>(() =>
                                mirrorWorkerRunner.RunAsync(
                                    run.Id, plan.Id, later.Id, observationId,
                                    first.WorkpackId, "repo-1",
                                    targetReceipt.EvidenceSha256,
                                    candidate.RequestedBy, activeWorker,
                                    default));
                            Assert.Equal(
                                failAfterImport
                                    ? "LaterCheckpointMirrorWorkerInjectedPostImportFailure"
                                    : "LaterCheckpointMirrorWorkerInjectedImportFailure",
                                thrown.Code);
                            var recovery = await verifiedDb.Database
                                .SqlQuery<string>($"""
                                    SELECT state AS "Value"
                                    FROM git_later_checkpoint_mirror_leases
                                    WHERE mirror_id = {staging.PhysicalMirrorId}
                                    """).SingleAsync();
                            Assert.Equal("RecoveryRequired", recovery);
                            var evidenceCount = await verifiedDb.Database
                                .SqlQuery<long>($"""
                                    SELECT count(*)::bigint AS "Value"
                                    FROM git_later_checkpoint_mirror_verified_evidence
                                    WHERE mirror_id = {staging.PhysicalMirrorId}
                                    """).SingleAsync();
                            Assert.Equal(0L, evidenceCount);
                            var interruptedEvents = await verifiedDb.Database
                                .SqlQuery<string>($"""
                                    SELECT type AS "Value"
                                    FROM git_later_checkpoint_mirror_lease_events
                                    WHERE mirror_id = {staging.PhysicalMirrorId}
                                    """).ToArrayAsync();
                            Assert.Contains("Acquired", interruptedEvents);
                            // The local import can fail before the first
                            // expensive SQL/physical pulse completes. A
                            // zero-renewal Acquired -> Quarantined chain
                            // is valid; the successful worker tests require
                            // multiple completed Renewed events separately.
                            Assert.All(interruptedEvents, item =>
                                Assert.Contains(item,
                                    new[] { "Acquired", "Renewed", "Quarantined" }));
                            Assert.Equal(1, interruptedEvents.Count(x =>
                                x == "Quarantined"));
                            Assert.DoesNotContain("Verified", interruptedEvents);
                            var recoveryInspector = auditScope.ServiceProvider
                                .GetRequiredService<
                                    PostgresGitIncrementalLaterCheckpointMirrorRecoveryInspector>();
                            var diagnostic = await recoveryInspector.ReadAsync(
                                run.Id, plan.Id, later.Id, observationId,
                                first.WorkpackId, "repo-1",
                                targetReceipt.EvidenceSha256,
                                candidate.RequestedBy, default);
                            Assert.Equal("ImportWorkerFailed",
                                diagnostic.FailureCode);
                            Assert.Equal(activeWorker, diagnostic.WorkerId);
                            Assert.Equal(1L, diagnostic.LeaseGeneration);
                            Assert.Equal(staging.PhysicalMirrorId,
                                diagnostic.PhysicalMirrorId);
                            Assert.Equal(
                                failAfterImport
                                    ? GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity
                                        .Intact
                                    : GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity
                                        .Uninspectable, diagnostic.PhysicalState);
                            if (failAfterImport)
                            {
                                // A fully promoted real UUID.git can be
                                // intact even though its worker crashed
                                // before issuing Verified. This is *never*
                                // authorization to adopt/overwrite it.
                                var auditor = auditScope.ServiceProvider
                                    .GetRequiredService<
                                        IGitIncrementalLaterCheckpointMirrorPhysicalAuditor>();
                                var physicalAudit = await auditor.AuditAsync(
                                    staging, committed, default);
                                Assert.Equal(physicalAudit.ObjectSetSha256,
                                    diagnostic.ObjectSetSha256);
                                Assert.Equal(physicalAudit.ObjectCount,
                                    diagnostic.ObjectCount);
                                // The second independent read occurs
                                // after the inspection's timestamp: compare
                                // its exact real physical graph SHA/count
                                // rather than passing a *later* receipt
                                // into the earlier inspection's time seal.
                                Assert.False(diagnostic.CanReAdmit);
                                Assert.False(diagnostic.DurableRecoveryApprovalPersisted);
                            }
                            else
                            {
                                Assert.Null(diagnostic.ObjectSetSha256);
                                GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity
                                    .Verify(diagnostic, staging, committed,
                                        null);
                            }
                            Assert.Equal(interruptedEvents.Length,
                                diagnostic.LeaseEventCount);
                            Assert.Equal(interruptedEvents.Count(x => x == "Renewed"),
                                diagnostic.RenewedEventCount);
                            Assert.False(diagnostic.DurableRecoveryApprovalPersisted ||
                                diagnostic.CanReAdmit || diagnostic.CanMaterialize ||
                                diagnostic.CanPush || diagnostic.CanRetry ||
                                diagnostic.CanDispatch || diagnostic.MigrationComplete);
                            // #122: independent F-031 human review
                            // persists a decision ONLY, never releases
                            // the quarantined worker or adopts UUID.git.
                            var journal = auditScope.ServiceProvider
                                .GetRequiredService<
                                    PostgresGitIncrementalLaterCheckpointMirrorRecoveryReviewJournal>();
                            var stableCandidate =
                                GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity
                                    .ReviewCandidateSha256(diagnostic);
                            Assert.Equal(stableCandidate,
                                GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity
                                    .ReviewCandidateSha256(
                                        diagnostic with {
                                            InspectedAtUtc =
                                                diagnostic.InspectedAtUtc.AddSeconds(1)
                                        }));
                            var reviewDecision = failAfterImport
                                ? GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
                                    .ApprovedForIsolationReview
                                : GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
                                    .Rejected;
                            Assert.Equal(
                                "LaterCheckpointMirrorRecoveryReviewInspectionChanged",
                                (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                                    () => journal.RecordAsync(
                                        run.Id, plan.Id, later.Id, observationId,
                                        first.WorkpackId, "repo-1",
                                        targetReceipt.EvidenceSha256,
                                        candidate.RequestedBy, reviewerId,
                                        reviewDecision, new string('0',64),
                                        default))).Code);
                            // A JWT actor who is also the original bundle
                            // producer is forbidden to review their own
                            // orphan. This is a policy conflict, not a
                            // malformed request, and must agree with HTTP 409.
                            Assert.Equal(
                                "LaterCheckpointMirrorRecoveryReviewIndependentReviewerRequired",
                                (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                                    () => journal.RecordAsync(
                                        run.Id, plan.Id, later.Id, observationId,
                                        first.WorkpackId, "repo-1",
                                        targetReceipt.EvidenceSha256,
                                        candidate.RequestedBy,
                                        candidate.RequestedBy,
                                        reviewDecision, stableCandidate,
                                        default))).Code);
                            GitIncrementalLaterCheckpointMirrorRecoveryDecision
                                humanDecision;
                            if (failAfterImport)
                            {
                                // JWT subject comes from server claims.
                                // A stable candidate survives the second
                                // full PostgreSQL + physical Git audit.
                                var rootPath =
                                    "/api/incremental-preparations/" + run.Id +
                                    "/operation-plans/" + plan.Id +
                                    "/source-sync-checkpoints/" + later.Id +
                                    "/mirror-recovery";
                                var queryPath = rootPath + "/candidate" +
                                    "?targetObservationId=" + observationId +
                                    "&workpackId=" + first.WorkpackId +
                                    "&sourceRepositoryId=repo-1" +
                                    "&originalBundleActorId=" +
                                        candidate.RequestedBy +
                                    "&expectedTargetReceiptSha256=" +
                                        targetReceipt.EvidenceSha256;
                                using (var candidateGet = await SendAsync(
                                    HttpMethod.Get, queryPath))
                                {
                                    Assert.Equal(System.Net.HttpStatusCode.OK,
                                        candidateGet.StatusCode);
                                    using var json = JsonDocument.Parse(
                                        await candidateGet.Content.ReadAsStringAsync());
                                    Assert.Equal(stableCandidate,
                                        json.RootElement.GetProperty(
                                            "reviewCandidateSha256").GetString());
                                    Assert.Equal("Intact",
                                        json.RootElement.GetProperty(
                                            "physicalState").GetString());
                                    Assert.False(json.RootElement.GetProperty(
                                        "canReAdmit").GetBoolean());
                                    Assert.DoesNotContain(
                                        "originalLeaseToken",
                                        json.RootElement.GetRawText());
                                }
                                var request = new {
                                    targetObservationId = observationId,
                                    workpackId = first.WorkpackId,
                                    sourceRepositoryId = "repo-1",
                                    originalBundleActorId =
                                        candidate.RequestedBy,
                                    expectedTargetReceiptSha256 =
                                        targetReceipt.EvidenceSha256,
                                    expectedInspectionSha256 = stableCandidate,
                                    decision = reviewDecision
                                };
                                using (var wrongReviewer = await SendAsync(
                                    HttpMethod.Post,
                                    rootPath + "/decision", request,
                                    requesterToken))
                                {
                                    Assert.Equal(System.Net.HttpStatusCode.Conflict,
                                        wrongReviewer.StatusCode);
                                }
                                using (var otherSuperadmin = await SendAsync(
                                    HttpMethod.Post,
                                    rootPath + "/decision", request,
                                    otherReviewerToken))
                                {
                                    Assert.Equal(System.Net.HttpStatusCode.Conflict,
                                        otherSuperadmin.StatusCode);
                                    Assert.Contains(
                                        "LaterCheckpointMirrorRecoveryReviewIndependentReviewerRequired",
                                        await otherSuperadmin.Content.ReadAsStringAsync());
                                }
                                using (var accepted = await SendAsync(
                                    HttpMethod.Post,
                                    rootPath + "/decision", request))
                                {
                                    Assert.Equal(System.Net.HttpStatusCode.OK,
                                        accepted.StatusCode);
                                    using var json = JsonDocument.Parse(
                                        await accepted.Content.ReadAsStringAsync());
                                    Assert.Equal(reviewDecision,
                                        json.RootElement.GetProperty(
                                            "decision").GetString());
                                    Assert.False(json.RootElement.GetProperty(
                                        "canReAdmit").GetBoolean());
                                    Assert.False(json.RootElement.GetProperty(
                                        "canPush").GetBoolean());
                                    Assert.DoesNotContain("originalLeaseToken",
                                        json.RootElement.GetRawText());
                                }
                                var savedAt = await verifiedDb.Database
                                    .SqlQuery<DateTimeOffset>($"""
                                        SELECT reviewed_at_utc AS "Value"
                                        FROM git_later_checkpoint_mirror_recovery_decisions
                                        WHERE mirror_id = {staging.PhysicalMirrorId}
                                        """).SingleAsync();
                                humanDecision =
                                    GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
                                        .Seal(diagnostic, candidate.RequestedBy,
                                            reviewerId, reviewDecision, savedAt);
                            }
                            else
                            {
                                humanDecision = await journal.RecordAsync(
                                    run.Id, plan.Id, later.Id, observationId,
                                    first.WorkpackId, "repo-1",
                                    targetReceipt.EvidenceSha256,
                                    candidate.RequestedBy, reviewerId,
                                    reviewDecision, stableCandidate, default);
                            }
                            Assert.Equal(reviewerId, humanDecision.ReviewerId);
                            Assert.Equal(reviewDecision, humanDecision.Decision);
                            Assert.Equal(stableCandidate,
                                humanDecision.InspectionEvidenceSha256);
                            Assert.Equal(diagnostic.LeaseEventHistorySha256,
                                humanDecision.LeaseEventHistorySha256);
                            Assert.False(humanDecision.CanReAdmit ||
                                humanDecision.CanMaterialize ||
                                humanDecision.CanPush || humanDecision.CanRetry ||
                                humanDecision.CanDispatch ||
                                humanDecision.MigrationComplete);
                            var decisionRows = await verifiedDb.Database
                                .SqlQuery<long>($"""
                                    SELECT count(*)::bigint AS "Value"
                                    FROM git_later_checkpoint_mirror_recovery_decisions
                                    WHERE mirror_id = {staging.PhysicalMirrorId}
                                        AND reviewer_id = {reviewerId}
                                        AND evidence_sha256 = {humanDecision.EvidenceSha256}
                                        AND NOT can_readmit AND NOT can_push
                                        AND NOT can_retry AND NOT can_dispatch
                                        AND NOT migration_complete
                                    """).SingleAsync();
                            Assert.Equal(1L, decisionRows);
                            var decisionEvents = await verifiedDb.Database
                                .SqlQuery<long>($"""
                                    SELECT count(*)::bigint AS "Value"
                                    FROM git_later_checkpoint_mirror_recovery_decision_events
                                    WHERE mirror_id = {staging.PhysicalMirrorId}
                                        AND reviewer_id = {reviewerId}
                                        AND evidence_sha256 = {humanDecision.EvidenceSha256}
                                    """).SingleAsync();
                            Assert.Equal(1L, decisionEvents);

                            // #123: a true later-epoch, reviewed and
                            // physically intact RecoveryRequired orphan can
                            // create ONE immutable PRE-MOVE SQL intent.
                            // No filesystem isolation is invoked by it.
                            var isolationIntent = new
                                PostgresGitIncrementalLaterCheckpointMirrorIsolationIntentJournal(
                                    verifiedDb, new(true), recoveryInspector,
                                    auditScope.ServiceProvider.GetRequiredService<
                                        PostgresGitIncrementalLaterCheckpointMirrorAdmissionPreflight>(),
                                    auditScope.ServiceProvider.GetRequiredService<
                                        PostgresGitIncrementalLaterCheckpointMirrorLeaseStore>());
                            if (failAfterImport)
                            {
                                Phase("worker + reviewed recovery complete", targetReceipt.ObservedAtUtc);
                                var prepared = await isolationIntent.PrepareAsync(
                                    run.Id, plan.Id, later.Id, observationId,
                                    first.WorkpackId, "repo-1",
                                    targetReceipt.EvidenceSha256,
                                    candidate.RequestedBy, default);
                                Assert.Equal(humanDecision.EvidenceSha256,
                                    prepared.RecoveryDecisionSha256);
                                Assert.Equal(stableCandidate,
                                    prepared.InspectionSha256);
                                Assert.Equal(diagnostic.LeaseEventHistorySha256,
                                    prepared.LeaseEventHistorySha256);
                                Assert.Equal(diagnostic.ObjectSetSha256,
                                    prepared.ObjectSetSha256);
                                Assert.Equal(staging.PhysicalMirrorId,
                                    prepared.PhysicalMirrorId);
                                Assert.False(prepared.CanReAdmit ||
                                    prepared.CanMaterialize || prepared.CanPush ||
                                    prepared.CanRetry || prepared.CanDispatch ||
                                    prepared.MigrationComplete);
                                Assert.Equal(1L, await verifiedDb.Database
                                    .SqlQuery<long>($"""
                                        SELECT count(*)::bigint AS "Value"
                                        FROM git_later_checkpoint_mirror_isolation_intents
                                        WHERE mirror_id = {staging.PhysicalMirrorId}
                                            AND intent_sha256 = {prepared.IntentSha256}
                                            AND NOT can_readmit AND NOT can_push
                                            AND NOT can_retry AND NOT can_dispatch
                                            AND NOT migration_complete
                                        """).SingleAsync());
                                Assert.Equal(1L, await verifiedDb.Database
                                    .SqlQuery<long>($"""
                                        SELECT count(*)::bigint AS "Value"
                                        FROM git_later_checkpoint_mirror_isolation_intent_events
                                        WHERE mirror_id = {staging.PhysicalMirrorId}
                                            AND intent_sha256 = {prepared.IntentSha256}
                                            AND type = 'Prepared'
                                        """).SingleAsync());
                                Assert.Equal(0L, await verifiedDb.Database
                                    .SqlQuery<long>($"""
                                        SELECT count(*)::bigint AS "Value"
                                        FROM git_later_checkpoint_mirror_isolation_receipts
                                        WHERE mirror_id = {staging.PhysicalMirrorId}
                                        """).SingleAsync());

                                // These pre-move forgery/restart
                                // probes run in an independent F-031
                                // epoch, so a long physical post-isolation
                                // audit still has its original 5m budget.
                                if (preMoveOnly)
                                {
                                // Direct SQL is NOT a substitute for the
                                // independently audited physical POSIX
                                // session. The immutable DB guard must
                                // reject a forged terminal receipt whose
                                // worker identity differs from the
                                // actually quarantined F-061 generation,
                                // even when ALL other F-035/Prepared
                                // hashes and plausible object counts
                                // match. No receipt or event may appear.
                                var forgedSqlReceipt = await Assert.ThrowsAsync<
                                    Npgsql.PostgresException>(() =>
                                    verifiedDb.Database.ExecuteSqlInterpolatedAsync($"""
                                        INSERT INTO git_later_checkpoint_mirror_isolation_receipts (
                                            mirror_id, workpack_id,
                                            isolation_directory_name,
                                            intent_sha256,
                                            recovery_decision_sha256,
                                            original_worker_id,
                                            original_lease_token,
                                            original_generation,
                                            bundle_sha256, bundle_bytes,
                                            reconciled_existing_quarantine,
                                            offline_receipt_sha256,
                                            physically_verified_object_sha256,
                                            physically_verified_object_count,
                                            isolated_at_utc, recorded_at_utc,
                                            evidence_sha256)
                                        VALUES (
                                            {staging.PhysicalMirrorId},
                                            {first.WorkpackId},
                                            {prepared.IsolationDirectoryName},
                                            {prepared.IntentSha256},
                                            {humanDecision.EvidenceSha256},
                                            {Guid.NewGuid()},
                                            {diagnostic.LeaseToken},
                                            {diagnostic.LeaseGeneration},
                                            {committed.BundleSha256},
                                            {committed.BundleBytes},
                                            {false}, {new string('c', 64)},
                                            {diagnostic.ObjectSetSha256},
                                            {diagnostic.ObjectCount},
                                            {prepared.PreparedAtUtc},
                                            {prepared.PreparedAtUtc.AddSeconds(1)},
                                            {new string('a', 64)})
                                        """));
                                Assert.Equal(
                                    Npgsql.PostgresErrorCodes.RaiseException,
                                    forgedSqlReceipt.SqlState);
                                Assert.Equal(0L, await verifiedDb.Database
                                    .SqlQuery<long>($"""
                                        SELECT count(*)::bigint AS "Value"
                                        FROM git_later_checkpoint_mirror_isolation_receipts
                                        WHERE mirror_id = {staging.PhysicalMirrorId}
                                        """).SingleAsync());

                                // A fabricated Isolated event before the
                                // source-locked physical receipt must fail
                                // independently of the source writer.
                                var forgedSqlEvent = await Assert.ThrowsAsync<
                                    Npgsql.PostgresException>(() =>
                                    verifiedDb.Database.ExecuteSqlInterpolatedAsync($"""
                                        INSERT INTO
                                            git_later_checkpoint_mirror_isolation_receipt_events (
                                                mirror_id, evidence_sha256,
                                                type, occurred_at_utc)
                                        VALUES ({staging.PhysicalMirrorId},
                                            {new string('b', 64)},
                                            'Isolated',
                                            {prepared.PreparedAtUtc})
                                        """));
                                Assert.Equal(
                                    Npgsql.PostgresErrorCodes.RaiseException,
                                    forgedSqlEvent.SqlState);
                                Assert.Equal(0L, await verifiedDb.Database
                                    .SqlQuery<long>($"""
                                        SELECT count(*)::bigint AS "Value"
                                        FROM git_later_checkpoint_mirror_isolation_receipt_events
                                        WHERE mirror_id = {staging.PhysicalMirrorId}
                                        """).SingleAsync());

                                // #124: reboot a DIFFERENT reader from
                                // actual source-locked durable SQL. No
                                // caller-provided decision/inspection/intent
                                // is allowed on this read path. Real
                                // UUID.bundle and current F-031/F-091/F-097
                                // provenance are checked again.
                                var restarted = new
                                    PostgresGitIncrementalLaterCheckpointMirrorPreparedReconciliationReader(
                                        verifiedDb, new(true),
                                        auditScope.ServiceProvider.GetRequiredService<
                                            PostgresGitIncrementalLaterCheckpointMirrorStagingPreflight>(),
                                        auditScope.ServiceProvider.GetRequiredService<
                                            PostgresGitIncrementalLaterCheckpointBundleHistory>(),
                                        auditScope.ServiceProvider.GetRequiredService<
                                            PostgresGitIncrementalLaterCheckpointMirrorAdmissionPreflight>(),
                                        auditScope.ServiceProvider.GetRequiredService<
                                            PostgresGitIncrementalLaterCheckpointMirrorLeaseStore>());
                                var recovered = await restarted.ReadAsync(
                                    run.Id, plan.Id, later.Id, observationId,
                                    first.WorkpackId, "repo-1",
                                    targetReceipt.EvidenceSha256,
                                    candidate.RequestedBy, default);
                                Assert.Equal(prepared, recovered.PreparedIntent);
                                Assert.Equal(humanDecision,
                                    recovered.HumanDecision);
                                Assert.Equal(staging.PhysicalMirrorId,
                                    recovered.Stage.PhysicalMirrorId);
                                Assert.Equal(diagnostic.ObjectSetSha256,
                                    recovered.ExpectedInspection.ObjectSetSha256);
                                Assert.Equal(diagnostic.LeaseEventHistorySha256,
                                    recovered.ExpectedInspection.LeaseEventHistorySha256);
                                Assert.False(recovered.PreparedIntent.CanReAdmit ||
                                    recovered.PreparedIntent.CanPush ||
                                    recovered.PreparedIntent.CanRetry ||
                                    recovered.PreparedIntent.CanDispatch);
                                // A fabricated UPDATE or event DELETE
                                // cannot erase/modify the crash fence.
                                var immutableIntent = await Assert.ThrowsAsync<
                                    Npgsql.PostgresException>(() =>
                                    verifiedDb.Database.ExecuteSqlInterpolatedAsync($"""
                                        UPDATE git_later_checkpoint_mirror_isolation_intents
                                        SET can_push = true
                                        WHERE mirror_id = {staging.PhysicalMirrorId}
                                        """));
                                Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException,
                                    immutableIntent.SqlState);
                                var immutableEvent = await Assert.ThrowsAsync<
                                    Npgsql.PostgresException>(() =>
                                    verifiedDb.Database.ExecuteSqlInterpolatedAsync($"""
                                        DELETE FROM
                                            git_later_checkpoint_mirror_isolation_intent_events
                                        WHERE mirror_id = {staging.PhysicalMirrorId}
                                        """));
                                Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException,
                                    immutableEvent.SqlState);
                                Assert.Equal("LaterCheckpointMirrorIsolationIntentAlreadyPrepared",
                                    (await Assert.ThrowsAsync<
                                        GitSourceSnapshotMirrorException>(() =>
                                        isolationIntent.PrepareAsync(
                                            run.Id, plan.Id, later.Id,
                                            observationId, first.WorkpackId,
                                            "repo-1",
                                            targetReceipt.EvidenceSha256,
                                            candidate.RequestedBy, default))).Code);
                                }

                            }
                            else
                            {
                                Assert.Equal("LaterCheckpointMirrorIsolationIntentNoIntactOrphan",
                                    (await Assert.ThrowsAsync<
                                        GitSourceSnapshotMirrorException>(() =>
                                        isolationIntent.PrepareAsync(
                                            run.Id, plan.Id, later.Id,
                                            observationId, first.WorkpackId,
                                            "repo-1",
                                            targetReceipt.EvidenceSha256,
                                            candidate.RequestedBy, default))).Code);
                            }

                            // The independently provenanced pre-move
                            // branch retains every mutation/replay denial;
                            // the post-isolation case reuses only durable
                            // immutable evidence, never those forged writes.
                            if (!failAfterImport || preMoveOnly)
                            {
                            Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException,
                                (await Assert.ThrowsAsync<Npgsql.PostgresException>(
                                    () => verifiedDb.Database.ExecuteSqlInterpolatedAsync($"""
                                        UPDATE git_later_checkpoint_mirror_recovery_decisions
                                        SET can_push = {true}
                                        WHERE mirror_id = {staging.PhysicalMirrorId}
                                        """))).SqlState);
                            Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException,
                                (await Assert.ThrowsAsync<Npgsql.PostgresException>(
                                    () => verifiedDb.Database.ExecuteSqlInterpolatedAsync($"""
                                        UPDATE git_later_checkpoint_mirror_recovery_decision_events
                                        SET decision = {"Rejected"}
                                        WHERE mirror_id = {staging.PhysicalMirrorId}
                                        """))).SqlState);
                            Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException,
                                (await Assert.ThrowsAsync<Npgsql.PostgresException>(
                                    () => verifiedDb.Database.ExecuteSqlInterpolatedAsync($"""
                                        DELETE FROM git_later_checkpoint_mirror_recovery_decisions
                                        WHERE mirror_id = {staging.PhysicalMirrorId}
                                        """))).SqlState);
                            Assert.Equal("LaterCheckpointMirrorRecoveryReviewAlreadyRecorded",
                                (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                                    () => journal.RecordAsync(
                                        run.Id, plan.Id, later.Id, observationId,
                                        first.WorkpackId, "repo-1",
                                        targetReceipt.EvidenceSha256,
                                        candidate.RequestedBy, reviewerId,
                                        reviewDecision, stableCandidate,
                                        default))).Code);
                            Assert.Equal("RecoveryRequired",
                                await verifiedDb.Database.SqlQuery<string>($"""
                                    SELECT state AS "Value"
                                    FROM git_later_checkpoint_mirror_leases
                                    WHERE mirror_id = {staging.PhysicalMirrorId}
                                    """).SingleAsync());
                            Assert.Equal(0L, await verifiedDb.Database
                                .SqlQuery<long>($"""
                                    SELECT count(*)::bigint AS "Value"
                                    FROM git_later_checkpoint_mirror_verified_evidence
                                    WHERE mirror_id = {staging.PhysicalMirrorId}
                                    """).SingleAsync());
                            await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                                () => recoveryInspector.ReadAsync(
                                    run.Id, plan.Id, later.Id, observationId,
                                    first.WorkpackId, "foreign-repo",
                                    targetReceipt.EvidenceSha256,
                                    candidate.RequestedBy, default));

                            }
                            if (preMoveOnly)
                            {
                                Phase("pre-move negatives complete",
                                    targetReceipt.ObservedAtUtc);
                                // The post-isolation SHA1 crash and SHA256
                                // three-replica races run in OTHER genuine
                                // target-observation test epochs.
                                return;
                            }

                            if (failAfterImport)
                            {
                                Phase("prepared SQL + pre-move negatives done", targetReceipt.ObservedAtUtc);
                                // #124: REAL trusted physical quarantine +
                                // source-first SQL terminal receipt. SHA1
                                // exercises the crash window AFTER kernel
                                // rename but BEFORE the SQL transaction.
                                // SHA256 executes the normal path.
                                var latestReader = new
                                    PostgresGitIncrementalLaterCheckpointMirrorPreparedReconciliationReader(
                                        verifiedDb, new(true),
                                        auditScope.ServiceProvider.GetRequiredService<
                                            PostgresGitIncrementalLaterCheckpointMirrorStagingPreflight>(),
                                        auditScope.ServiceProvider.GetRequiredService<
                                            PostgresGitIncrementalLaterCheckpointBundleHistory>(),
                                        auditScope.ServiceProvider.GetRequiredService<
                                            PostgresGitIncrementalLaterCheckpointMirrorAdmissionPreflight>(),
                                        auditScope.ServiceProvider.GetRequiredService<
                                            PostgresGitIncrementalLaterCheckpointMirrorLeaseStore>());
                                var freshPrepared = await latestReader.ReadAsync(
                                    run.Id, plan.Id, later.Id,
                                    observationId, first.WorkpackId,
                                    "repo-1", targetReceipt.EvidenceSha256,
                                    candidate.RequestedBy, default);
                                var localSession = new
                                    LocalGitIncrementalLaterCheckpointMirrorOrphanIsolator(
                                        physicalMirror!.BundleRoot,
                                        physicalMirror.VerifiedMirrorRoot,
                                        enabled: true);
                                if (oidLength == 40)
                                {
                                    var pendingHistory = freshPrepared;
                                    var crash = await Assert.ThrowsAsync<
                                        InvalidOperationException>(() =>
                                        localSession.WithVerifiedIsolationAsync(
                                            pendingHistory.Stage,
                                            pendingHistory.DurableBundle,
                                            pendingHistory.ExpectedInspection,
                                            pendingHistory.HumanDecision,
                                            pendingHistory.PreparedIntent,
                                            (physicalProof, token) =>
                                                Task.FromException<string>(
                                                    new InvalidOperationException(
                                                        "SimulatedProcessCrashAfterRename")),
                                            default));
                                    Assert.Equal("SimulatedProcessCrashAfterRename",
                                        crash.Message);
                                    Assert.Equal(0L, await verifiedDb.Database
                                        .SqlQuery<long>($"""
                                            SELECT count(*)::bigint AS "Value"
                                            FROM git_later_checkpoint_mirror_isolation_receipts
                                            WHERE mirror_id = {staging.PhysicalMirrorId}
                                            """).SingleAsync());
                                    Assert.False(Directory.Exists(Path.Combine(
                                        physicalMirror.VerifiedMirrorRoot,
                                        staging.PhysicalMirrorId.ToString("N") +
                                            ".git")));
                                    // A real filesystem attacker/corruption
                                    // between rename and SQL receipt must
                                    // not be silently healed or accepted
                                    // even when Git graph SHAs still match.
                                    var quarantinedMarker = Path.Combine(
                                        physicalMirror.VerifiedMirrorRoot,
                                        pendingHistory.PreparedIntent.IsolationDirectoryName,
                                        "agic-f061v2-reviewed-isolation-intent");
                                    var sealedMarker =
                                        await File.ReadAllBytesAsync(
                                            quarantinedMarker);
                                    // A legitimate kernel rename + marker
                                    // has an actual, verified crash-state
                                    // classification even before SQL INSERT.
                                    Assert.Equal(
                                        LocalGitIncrementalLaterCheckpointMirrorOrphanIsolator
                                            .PreparedIsolatedIntact,
                                        await localSession.InspectPreparedPathsAsync(
                                            pendingHistory.Stage,
                                            pendingHistory.DurableBundle,
                                            pendingHistory.ExpectedInspection,
                                            pendingHistory.HumanDecision,
                                            pendingHistory.PreparedIntent,
                                            default));
                                    await File.WriteAllTextAsync(
                                        quarantinedMarker,
                                        "forged-review-or-prepared-intent");
                                    Assert.Equal(
                                        LocalGitIncrementalLaterCheckpointMirrorOrphanIsolator
                                            .PreparedIsolatedUntrusted,
                                        await localSession.InspectPreparedPathsAsync(
                                            pendingHistory.Stage,
                                            pendingHistory.DurableBundle,
                                            pendingHistory.ExpectedInspection,
                                            pendingHistory.HumanDecision,
                                            pendingHistory.PreparedIntent,
                                            default));
                                    var markerDenied = await Assert.ThrowsAsync<
                                        GitSourceSnapshotMirrorException>(() =>
                                        localSession.WithVerifiedIsolationAsync(
                                            pendingHistory.Stage,
                                            pendingHistory.DurableBundle,
                                            pendingHistory.ExpectedInspection,
                                            pendingHistory.HumanDecision,
                                            pendingHistory.PreparedIntent,
                                            (p, token) =>
                                                Task.FromResult("forged"),
                                            default));
                                    Assert.Equal(
                                        "LaterCheckpointMirrorIsolationMarkerInvalid",
                                        markerDenied.Code);
                                    Assert.Equal(0L, await verifiedDb.Database
                                        .SqlQuery<long>($"""
                                            SELECT count(*)::bigint AS "Value"
                                            FROM git_later_checkpoint_mirror_isolation_receipts
                                            WHERE mirror_id = {staging.PhysicalMirrorId}
                                            """).SingleAsync());
                                    await File.WriteAllBytesAsync(
                                        quarantinedMarker, sealedMarker);
                                    Assert.Equal(
                                        LocalGitIncrementalLaterCheckpointMirrorOrphanIsolator
                                            .PreparedIsolatedIntact,
                                        await localSession.InspectPreparedPathsAsync(
                                            pendingHistory.Stage,
                                            pendingHistory.DurableBundle,
                                            pendingHistory.ExpectedInspection,
                                            pendingHistory.HumanDecision,
                                            pendingHistory.PreparedIntent,
                                            default));

                                    // Cancellation arrives immediately
                                    // after the second physical Git pass
                                    // while constructing the trusted
                                    // handoff proof. No SQL callback is
                                    // invoked and no receipt is durable.
                                    // A fresh uncancelled call below
                                    // MUST still reconcile same marker.
                                    using var canceledHandoff =
                                        new CancellationTokenSource();
                                    var canceledClock = new
                                        CancelAtPhysicalHandoffClock(
                                            canceledHandoff);
                                    var canceledSession = new
                                        LocalGitIncrementalLaterCheckpointMirrorOrphanIsolator(
                                            physicalMirror.BundleRoot,
                                            physicalMirror.VerifiedMirrorRoot,
                                            enabled: true, clock: canceledClock);
                                    var callbacksAfterCancellation = 0;
                                    await Assert.ThrowsAnyAsync<
                                        OperationCanceledException>(() =>
                                        canceledSession.WithVerifiedIsolationAsync(
                                            pendingHistory.Stage,
                                            pendingHistory.DurableBundle,
                                            pendingHistory.ExpectedInspection,
                                            pendingHistory.HumanDecision,
                                            pendingHistory.PreparedIntent,
                                            (proof, token) =>
                                            {
                                                Interlocked.Increment(
                                                    ref callbacksAfterCancellation);
                                                return Task.FromResult("must not run");
                                            },
                                            canceledHandoff.Token));
                                    Assert.Equal(0, callbacksAfterCancellation);
                                    Assert.Equal(0L, await verifiedDb.Database
                                        .SqlQuery<long>($"""
                                            SELECT count(*)::bigint AS "Value"
                                            FROM git_later_checkpoint_mirror_isolation_receipts
                                            WHERE mirror_id = {staging.PhysicalMirrorId}
                                            """).SingleAsync());
                                    Assert.False(Directory.Exists(Path.Combine(
                                        physicalMirror.VerifiedMirrorRoot,
                                        staging.PhysicalMirrorId.ToString("N") +
                                            ".git")));
                                }
                                var isolation = new
                                    PostgresGitIncrementalLaterCheckpointMirrorIsolationCoordinator(
                                        verifiedDb, new(true), latestReader,
                                        auditScope.ServiceProvider.GetRequiredService<
                                            PostgresGitIncrementalLaterCheckpointMirrorAdmissionPreflight>(),
                                        auditScope.ServiceProvider.GetRequiredService<
                                            PostgresGitIncrementalLaterCheckpointMirrorLeaseStore>(),
                                        localSession);
                                GitIncrementalLaterCheckpointMirrorRecordedIsolationReceipt
                                    isolated;
                                if (oidLength == 64)
                                {
                                    // Three ACTUAL independent PostgreSQL
                                    // contexts race on one Prepared row and
                                    // same POSIX root lock. Exactly ONE can
                                    // record the immutable Isolated event.
                                    // Losers must observe only the original
                                    // completed receipt, never a second
                                    // rename, new lease or SQL event.
                                    // Bound the distributed contender test:
                                    // an unexpected PostgreSQL advisory-lock
                                    // or POSIX-root deadlock must FAIL this
                                    // quality gate, not hold CI indefinitely.
                                    using var replicaDeadline =
                                        new CancellationTokenSource(
                                            TimeSpan.FromMinutes(3));
                                    var go = new TaskCompletionSource(
                                        TaskCreationOptions.RunContinuationsAsynchronously);
                                    var replicas = Enumerable.Range(0, 3)
                                        .Select(async _ =>
                                        {
                                            await using var replicaScope =
                                                api.Host.Services.CreateAsyncScope();
                                            var sp = replicaScope.ServiceProvider;
                                            var replicaDb = sp.GetRequiredService<
                                                AppDbContext>();
                                            var replicaReader = new
                                                PostgresGitIncrementalLaterCheckpointMirrorPreparedReconciliationReader(
                                                    replicaDb, new(true),
                                                    sp.GetRequiredService<
                                                        PostgresGitIncrementalLaterCheckpointMirrorStagingPreflight>(),
                                                    sp.GetRequiredService<
                                                        PostgresGitIncrementalLaterCheckpointBundleHistory>(),
                                                    sp.GetRequiredService<
                                                        PostgresGitIncrementalLaterCheckpointMirrorAdmissionPreflight>(),
                                                    sp.GetRequiredService<
                                                        PostgresGitIncrementalLaterCheckpointMirrorLeaseStore>());
                                            var replicaCoordinator = new
                                                PostgresGitIncrementalLaterCheckpointMirrorIsolationCoordinator(
                                                    replicaDb, new(true),
                                                    replicaReader,
                                                    sp.GetRequiredService<
                                                        PostgresGitIncrementalLaterCheckpointMirrorAdmissionPreflight>(),
                                                    sp.GetRequiredService<
                                                        PostgresGitIncrementalLaterCheckpointMirrorLeaseStore>(),
                                                    new LocalGitIncrementalLaterCheckpointMirrorOrphanIsolator(
                                                        physicalMirror.BundleRoot,
                                                        physicalMirror.VerifiedMirrorRoot,
                                                        enabled: true));
                                            await go.Task;
                                            try
                                            {
                                                return await replicaCoordinator.RunAsync(
                                                    run.Id, plan.Id, later.Id,
                                                    observationId, first.WorkpackId,
                                                    "repo-1",
                                                    targetReceipt.EvidenceSha256,
                                                    candidate.RequestedBy,
                                                    replicaDeadline.Token);
                                            }
                                            catch (GitSourceSnapshotMirrorException ex)
                                                when (ex.Code is
                                                    "LaterCheckpointMirrorIsolationReconciliationAlreadyRecorded"
                                                    or "LaterCheckpointMirrorIsolationCoordinatorAlreadyRecorded")
                                            {
                                                return null;
                                            }
                                        }).ToArray();
                                    go.SetResult();
                                    var replies = await Task.WhenAll(replicas)
                                        .WaitAsync(replicaDeadline.Token);
                                    Assert.Single(replies.Where(x => x is not null));
                                    isolated = replies.Single(x => x is not null)!;
                                }
                                else
                                {
                                    isolated = await isolation.RunAsync(
                                        run.Id, plan.Id, later.Id, observationId,
                                        first.WorkpackId, "repo-1",
                                        targetReceipt.EvidenceSha256,
                                        candidate.RequestedBy, default);
                                }
                                Phase("POSIX isolation committed", targetReceipt.ObservedAtUtc);
                                Assert.True(isolated.DurableIsolationRecorded);
                                Assert.Equal(oidLength == 40,
                                    isolated.ReconciledExistingQuarantine);
                                Assert.Equal(humanDecision.EvidenceSha256,
                                    isolated.RecoveryDecisionSha256);
                                Assert.Equal(freshPrepared.PreparedIntent.IntentSha256,
                                    isolated.PreparedIntentSha256);
                                Assert.Equal(diagnostic.ObjectSetSha256,
                                    isolated.ObjectSetSha256);
                                Assert.Equal(staging.PhysicalMirrorId,
                                    isolated.PhysicalMirrorId);
                                Assert.False(isolated.CanReAdmit ||
                                    isolated.CanMaterialize ||
                                    isolated.CanPush || isolated.CanRetry ||
                                    isolated.CanDispatch ||
                                    isolated.MigrationComplete);
                                Assert.True(Directory.Exists(Path.Combine(
                                    physicalMirror.VerifiedMirrorRoot,
                                    isolated.IsolationDirectoryName)));
                                Assert.False(Directory.Exists(Path.Combine(
                                    physicalMirror.VerifiedMirrorRoot,
                                    staging.PhysicalMirrorId.ToString("N") +
                                        ".git")));
                                Assert.Equal(1L, await verifiedDb.Database
                                    .SqlQuery<long>($"""
                                        SELECT count(*)::bigint AS "Value"
                                        FROM git_later_checkpoint_mirror_isolation_receipts
                                        WHERE mirror_id = {staging.PhysicalMirrorId}
                                            AND evidence_sha256 = {isolated.EvidenceSha256}
                                            AND intent_sha256 = {freshPrepared.PreparedIntent.IntentSha256}
                                            AND original_worker_id = {diagnostic.WorkerId}
                                            AND original_lease_token = {diagnostic.LeaseToken}
                                            AND original_generation = {diagnostic.LeaseGeneration}
                                            AND reconciled_existing_quarantine =
                                                {oidLength == 40}
                                            AND NOT can_readmit AND NOT can_push
                                            AND NOT can_retry AND NOT can_dispatch
                                            AND NOT migration_complete
                                        """).SingleAsync());
                                Assert.Equal(1L, await verifiedDb.Database
                                    .SqlQuery<long>($"""
                                        SELECT count(*)::bigint AS "Value"
                                        FROM git_later_checkpoint_mirror_isolation_receipt_events
                                        WHERE mirror_id = {staging.PhysicalMirrorId}
                                            AND evidence_sha256 = {isolated.EvidenceSha256}
                                            AND type = 'Isolated'
                                        """).SingleAsync());
                                // #129: source-first post-Isolated
                                // reader requires the immutable SQL receipt
                                // AND exact event, without allowing the
                                // existing PRE-MOVE reader to replay rename.
                                Assert.Equal(
                                    "LaterCheckpointMirrorIsolationReconciliationAlreadyRecorded",
                                    (await Assert.ThrowsAsync<
                                        GitSourceSnapshotMirrorException>(() =>
                                        latestReader.ReadAsync(
                                            run.Id, plan.Id, later.Id,
                                            observationId, first.WorkpackId,
                                            "repo-1",
                                            targetReceipt.EvidenceSha256,
                                            candidate.RequestedBy,
                                            default))).Code);
                                Phase("post-isolation SQL read starts", targetReceipt.ObservedAtUtc);
                                var completedSource = await latestReader
                                    .ReadCompletedAsync(
                                        run.Id, plan.Id, later.Id,
                                        observationId, first.WorkpackId,
                                        "repo-1", targetReceipt.EvidenceSha256,
                                        candidate.RequestedBy, default);
                                Assert.Equal(freshPrepared.PreparedIntent,
                                    completedSource.PreparedIntent);
                                Assert.Equal(humanDecision,
                                    completedSource.HumanDecision);
                                Assert.Equal(isolated.ObjectSetSha256,
                                    completedSource.ExpectedInspection.ObjectSetSha256);
                                Assert.Equal(
                                    LocalGitIncrementalLaterCheckpointMirrorOrphanIsolator
                                        .PreparedIsolatedIntact,
                                    await localSession.InspectPreparedPathsAsync(
                                        completedSource.Stage,
                                        completedSource.DurableBundle,
                                        completedSource.ExpectedInspection,
                                        completedSource.HumanDecision,
                                        completedSource.PreparedIntent,
                                        default));
                                // Any foreign source scope must fail.
                                // The trusted underlying preflight may
                                // reject it before the post-isolation
                                // layer's own history checks.
                                await Assert.ThrowsAsync<
                                    GitSourceSnapshotMirrorException>(() =>
                                    latestReader.ReadCompletedAsync(
                                        run.Id, plan.Id, later.Id,
                                        observationId, first.WorkpackId,
                                        "foreign-repo",
                                        targetReceipt.EvidenceSha256,
                                        candidate.RequestedBy, default));
                                // #129: source-first SQL/physical bridge
                                // reads exact Isolated/decision/Prepared
                                // history before and after REAL POSIX
                                // root-locked double Git fsck and marker.
                                var postIsolation = new
                                    PostgresGitIncrementalLaterCheckpointMirrorPostIsolationInspector(
                                        verifiedDb, new(true), latestReader,
                                        localSession, TimeProvider.System);
                                Assert.Equal(
                                    "LaterCheckpointMirrorPostIsolationInspectorDisabled",
                                    (await Assert.ThrowsAsync<
                                        GitSourceSnapshotMirrorException>(() =>
                                        new PostgresGitIncrementalLaterCheckpointMirrorPostIsolationInspector(
                                            verifiedDb, new(false),
                                            latestReader, localSession,
                                            TimeProvider.System).ReadAsync(
                                                run.Id, plan.Id, later.Id,
                                                observationId, first.WorkpackId,
                                                "repo-1",
                                                targetReceipt.EvidenceSha256,
                                                candidate.RequestedBy,
                                                default))).Code);
                                var inspectedIsolation = await postIsolation.ReadAsync(
                                    run.Id, plan.Id, later.Id, observationId,
                                    first.WorkpackId, "repo-1",
                                    targetReceipt.EvidenceSha256,
                                    candidate.RequestedBy, default);
                                Assert.Equal(staging.PhysicalMirrorId,
                                    inspectedIsolation.PhysicalMirrorId);
                                Assert.Equal(isolated.EvidenceSha256,
                                    inspectedIsolation.IsolationReceiptSha256);
                                Assert.Equal(isolated.RecoveryDecisionSha256,
                                    inspectedIsolation.FirstReviewSha256);
                                Assert.Equal(isolated.PreparedIntentSha256,
                                    inspectedIsolation.PreparedIntentSha256);
                                Assert.Equal(isolated.ObjectSetSha256,
                                    inspectedIsolation.ObjectSetSha256);
                                Assert.Equal(isolated.ObjectCount,
                                    inspectedIsolation.ObjectCount);
                                Assert.Equal(
                                    LocalGitIncrementalLaterCheckpointMirrorOrphanIsolator
                                        .PreparedIsolatedIntact,
                                    inspectedIsolation.PhysicalState);
                                Assert.False(inspectedIsolation.DurableSecondReviewPersisted ||
                                    inspectedIsolation.CanReAdmit ||
                                    inspectedIsolation.CanMaterialize ||
                                    inspectedIsolation.CanPush ||
                                    inspectedIsolation.CanRetry ||
                                    inspectedIsolation.CanDispatch ||
                                    inspectedIsolation.MigrationComplete);
                                await Assert.ThrowsAsync<
                                    GitSourceSnapshotMirrorException>(() =>
                                    postIsolation.ReadAsync(
                                        run.Id, plan.Id, later.Id, observationId,
                                        first.WorkpackId, "foreign-repo",
                                        targetReceipt.EvidenceSha256,
                                        candidate.RequestedBy, default));
                                Phase("post-isolation source checked", targetReceipt.ObservedAtUtc);
                                // #129 adversarial POST-Isolated check:
                                // forging the durable Prepared marker
                                // AFTER SQL has committed must fail the
                                // READ-ONLY physical audit. Restore the
                                // exact bytes for subsequent history tests.
                                var committedMarker = Path.Combine(
                                    physicalMirror.VerifiedMirrorRoot,
                                    isolated.IsolationDirectoryName,
                                    "agic-f061v2-reviewed-isolation-intent");
                                var originalMarkerBytes =
                                    await File.ReadAllBytesAsync(committedMarker);
                                try
                                {
                                    await File.WriteAllTextAsync(committedMarker,
                                        "forged-second-review-marker");
                                    Assert.Equal(
                                        "LaterCheckpointMirrorPostIsolationPhysicalUntrusted",
                                        (await Assert.ThrowsAsync<
                                            GitSourceSnapshotMirrorException>(() =>
                                            postIsolation.ReadAsync(
                                                run.Id, plan.Id, later.Id,
                                                observationId, first.WorkpackId,
                                                "repo-1",
                                                targetReceipt.EvidenceSha256,
                                                candidate.RequestedBy,
                                                default))).Code);
                                }
                                finally
                                {
                                    await File.WriteAllBytesAsync(
                                        committedMarker, originalMarkerBytes);
                                }
                                Assert.Equal(isolated.EvidenceSha256,
                                    (await postIsolation.ReadAsync(
                                        run.Id, plan.Id, later.Id,
                                        observationId, first.WorkpackId,
                                        "repo-1",
                                        targetReceipt.EvidenceSha256,
                                        candidate.RequestedBy, default))
                                    .IsolationReceiptSha256);
                                Assert.False(Directory.Exists(Path.Combine(
                                    physicalMirror.VerifiedMirrorRoot,
                                    staging.PhysicalMirrorId.ToString("N") +
                                        ".git")));
                                // #127: after ONE trusted physical + SQL
                                // Isolated receipt, an additive audit-only
                                // generation assessment can be written and
                                // sealed without modifying the original
                                // RecoveryRequired lease. This does NOT
                                // authorize a new F-061 worker or import.
                                var generationSourceRead =
                                    GitIncrementalLaterCheckpointMirrorAdmissionIntegrity.Seal(
                                        staging, committed,
                                        isolated.RecordedAtUtc.AddSeconds(1));
                                var generationAssessment =
                                    GitIncrementalLaterCheckpointMirrorReadmissionAssessmentIntegrity.Seal(
                                        staging, committed, isolated,
                                        generationSourceRead,
                                        isolated.RecordedAtUtc.AddSeconds(2));
                                // #129: non-authorizing SECOND human review
                                // candidate. This is still a domain proof,
                                // NOT a JWT/SQL decision or a new F-061
                                // generation, importer or push capability.
                                var secondReviewer = Guid.NewGuid();
                                var secondReview =
                                    GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity.Seal(
                                        staging, committed, isolated,
                                        generationSourceRead, generationAssessment,
                                        diagnostic, humanDecision, secondReviewer,
                                        GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity
                                            .ApprovedForIndependentReview,
                                        isolated.RecordedAtUtc.AddSeconds(3));
                                GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity.Verify(
                                    secondReview, staging, committed, isolated,
                                    generationSourceRead, generationAssessment,
                                    diagnostic, humanDecision);
                                Assert.Equal(reviewerId, secondReview.FirstReviewerId);
                                Assert.Equal(secondReviewer, secondReview.SecondReviewerId);
                                Assert.Equal(generationAssessment.EvidenceSha256,
                                    secondReview.AssessmentEvidenceSha256);
                                Assert.Equal(isolated.EvidenceSha256,
                                    secondReview.IsolationReceiptSha256);
                                Assert.Equal(humanDecision.EvidenceSha256,
                                    secondReview.FirstDecisionSha256);
                                Assert.False(secondReview.DurableSecondReviewPersisted ||
                                    secondReview.CanReAdmit ||
                                    secondReview.CanMaterialize ||
                                    secondReview.CanPush || secondReview.CanRetry ||
                                    secondReview.CanDispatch ||
                                    secondReview.MigrationComplete);
                                foreach (var tamperedReview in new[] {
                                    secondReview with { CanReAdmit = true },
                                    secondReview with { CanPush = true },
                                    secondReview with { DurableSecondReviewPersisted = true },
                                    secondReview with { SecondReviewerId = reviewerId },
                                    secondReview with { FirstReviewerId = secondReviewer },
                                    secondReview with { Decision = "ReadyToPush" },
                                    secondReview with { IsolationReceiptSha256 =
                                        new string('0', 64) },
                                    secondReview with { AssessmentEvidenceSha256 =
                                        new string('0', 64) },
                                    secondReview with { OriginalGeneration = 2000 },
                                    secondReview with { ReviewedAtUtc =
                                        secondReview.ReviewedAtUtc.AddMinutes(3) },
                                    secondReview with { EvidenceSha256 =
                                        new string('0', 64) }
                                })
                                    Assert.Equal("LaterCheckpointMirrorSecondReviewInvalid",
                                        Assert.Throws<GitSourceSnapshotMirrorException>(
                                            () =>
                                                GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity
                                                    .Verify(tamperedReview,
                                                        staging, committed, isolated,
                                                        generationSourceRead,
                                                        generationAssessment,
                                                        diagnostic,
                                                        humanDecision)).Code);
                                foreach (var invalidReviewer in new[] {
                                    Guid.Empty, reviewerId, candidate.RequestedBy,
                                    diagnostic.WorkerId
                                })
                                    Assert.Equal("LaterCheckpointMirrorSecondReviewInvalid",
                                        Assert.Throws<GitSourceSnapshotMirrorException>(
                                            () =>
                                                GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity
                                                    .Seal(staging, committed, isolated,
                                                        generationSourceRead,
                                                        generationAssessment,
                                                        diagnostic, humanDecision,
                                                        invalidReviewer,
                                                        GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity
                                                            .ApprovedForIndependentReview,
                                                        secondReview.ReviewedAtUtc)).Code);
                                Assert.Equal("LaterCheckpointMirrorSecondReviewInvalid",
                                    Assert.Throws<GitSourceSnapshotMirrorException>(
                                        () =>
                                            GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity
                                                .Seal(staging, committed, isolated,
                                                    generationSourceRead,
                                                    generationAssessment with {
                                                        IsolationReceiptSha256 =
                                                            new string('f',64) },
                                                    diagnostic, humanDecision,
                                                    secondReviewer,
                                                    GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity
                                                        .ApprovedForIndependentReview,
                                                    secondReview.ReviewedAtUtc)).Code);
                                var rejectedSecondReview =
                                    GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity.Seal(
                                        staging, committed, isolated,
                                        generationSourceRead, generationAssessment,
                                        diagnostic, humanDecision, secondReviewer,
                                        GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity
                                            .RejectedForIndependentReview,
                                        secondReview.ReviewedAtUtc);
                                Assert.NotEqual(secondReview.EvidenceSha256,
                                    rejectedSecondReview.EvidenceSha256);
                                Assert.False(rejectedSecondReview.CanReAdmit);
                                // #129: SQL second-human-review FOUNDATION
                                // has two FKs + immutable CHECKs and is
                                // default DENY-ALL even against raw SQL.
                                // Domain-only hashes and guessed GUIDs
                                // cannot fake an authenticated reviewer.
                                Assert.Equal(2L, await verifiedDb.Database
                                    .SqlQuery<long>($"""
                                        SELECT count(*)::bigint AS "Value"
                                        FROM pg_constraint
                                        WHERE conname IN (
                                            'ck_f061v2e_second_review_safe',
                                            'ck_f061v2e_second_review_event_safe')
                                            AND contype = 'c'
                                        """).SingleAsync());
                                Assert.Equal(3L, await verifiedDb.Database
                                    .SqlQuery<long>($"""
                                        SELECT count(*)::bigint AS "Value"
                                        FROM pg_trigger
                                        WHERE tgname IN (
                                            'trg_f061v2e_second_review_closed',
                                            'trg_f061v2e_second_review_event_closed',
                                            'trg_f061v2e_second_review_session_closed')
                                            AND NOT tgisinternal
                                        """).SingleAsync());
                                // #130: the database CHECK is installed
                                // and validated on real PostgreSQL17, not
                                // only a domain-side no-op digest guard.
                                var sessionScopeV2 = await verifiedDb
                                    .Database.SqlQuery<string>($"""
                                        SELECT pg_get_constraintdef(c.oid)
                                            AS "Value"
                                        FROM pg_constraint c
                                        WHERE c.conname =
                                            'ck_f061v2e_second_review_session_scope_v2'
                                            AND c.conrelid =
                                                'git_later_checkpoint_mirror_second_review_session_consumptions'::regclass
                                            AND c.contype = 'c'
                                            AND c.convalidated
                                        """).SingleAsync();
                                Assert.Contains("review_session_id <> decision_nonce",
                                    sessionScopeV2);
                                Assert.Contains("mirror_id", sessionScopeV2);
                                Assert.Contains("review_evidence_sha256",
                                    sessionScopeV2);
                                Assert.Contains("'^[0-9a-f]{64}$'",
                                    sessionScopeV2);

                                // #130: store the domain-sealed,
                                // non-authorizing login-session intent
                                // digest in the durable session journal.
                                // The additive column must be NOT NULL
                                // even on idempotent schema bootstraps.
                                Assert.Equal(1L, await verifiedDb.Database
                                    .SqlQuery<long>($"""
                                        SELECT count(*)::bigint AS "Value"
                                        FROM information_schema.columns
                                        WHERE table_name =
                                            'git_later_checkpoint_mirror_second_review_session_consumptions'
                                            AND column_name =
                                                'session_intent_evidence_sha256'
                                            AND is_nullable = 'NO'
                                        """).SingleAsync());
                                var sessionIntentShaConstraint =
                                    await verifiedDb.Database
                                        .SqlQuery<string>($"""
                                            SELECT pg_get_constraintdef(c.oid)
                                                AS "Value"
                                            FROM pg_constraint c
                                            WHERE c.conname =
                                                'ck_f061v2e_second_review_session_intent_sha_v3'
                                                AND c.conrelid =
                                                    'git_later_checkpoint_mirror_second_review_session_consumptions'::regclass
                                                AND c.contype = 'c'
                                                AND c.convalidated
                                            """).SingleAsync();
                                Assert.Contains("session_intent_evidence_sha256",
                                    sessionIntentShaConstraint);
                                Assert.Contains("'^[0-9a-f]{64}$'",
                                    sessionIntentShaConstraint);

                                // #130: source-login canonical second
                                // and review/consumption millisecond
                                // timestamp precision must be enforced
                                // by a validated, installed PostgreSQL
                                // CHECK, not only by the C# domain.
                                var sessionPrecisionV4 = await verifiedDb
                                    .Database.SqlQuery<string>($"""
                                        SELECT pg_get_constraintdef(c.oid)
                                            AS "Value"
                                        FROM pg_constraint c
                                        WHERE c.conname =
                                            'ck_f061v2e_second_review_session_precision_v4'
                                            AND c.conrelid =
                                                'git_later_checkpoint_mirror_second_review_session_consumptions'::regclass
                                            AND c.contype = 'c'
                                            AND c.convalidated
                                        """).SingleAsync();
                                Assert.Contains("date_trunc('second'",
                                    sessionPrecisionV4);
                                Assert.Contains("date_trunc('milliseconds'",
                                    sessionPrecisionV4);
                                Assert.Contains("issued_at_utc",
                                    sessionPrecisionV4);
                                Assert.Contains("reviewed_at_utc",
                                    sessionPrecisionV4);
                                Assert.Contains("consumed_at_utc",
                                    sessionPrecisionV4);
                                Assert.Contains("expires_at_utc",
                                    sessionPrecisionV4);

                                // #129: the review's original terminal
                                // worker/token/gen and the #127 Assessed
                                // digest MUST be tied to the SAME source
                                // and physical row at the database level,
                                // not just independently well-formed SHA.
                                Assert.Equal(1L, await verifiedDb.Database
                                    .SqlQuery<long>($"""
                                        SELECT count(*)::bigint AS "Value"
                                        FROM pg_constraint c
                                        WHERE c.conname =
                                            'fk_f061v2e_second_review_generation_scope'
                                            AND c.conrelid =
                                                'git_later_checkpoint_mirror_second_reviews'::regclass
                                            AND c.confrelid =
                                                'git_later_checkpoint_mirror_generation_assessments'::regclass
                                            AND c.contype = 'f'
                                            AND cardinality(c.conkey) = 13
                                            AND cardinality(c.confkey) = 13
                                        """).SingleAsync());
                                var reviewScopeFk = await verifiedDb
                                    .Database.SqlQuery<string>($"""
                                        SELECT pg_get_constraintdef(c.oid)
                                            AS "Value"
                                        FROM pg_constraint c
                                        WHERE c.conname =
                                            'fk_f061v2e_second_review_generation_scope'
                                            AND c.conrelid =
                                                'git_later_checkpoint_mirror_second_reviews'::regclass
                                        """).SingleAsync();
                                Assert.Contains("assessment_evidence_sha256",
                                    reviewScopeFk);
                                Assert.Contains("original_lease_token",
                                    reviewScopeFk);
                                Assert.Contains("original_generation",
                                    reviewScopeFk);
                                Assert.Contains("recovery_decision_sha256",
                                    reviewScopeFk);
                                Assert.Contains("isolation_receipt_sha256",
                                    reviewScopeFk);
                                Assert.Equal(1L, await verifiedDb.Database
                                    .SqlQuery<long>($"""
                                        SELECT count(*)::bigint AS "Value"
                                        FROM pg_index i
                                        JOIN pg_class t ON t.oid = i.indexrelid
                                        WHERE t.relname =
                                            'uq_f061v2e_assessment_second_review_scope'
                                            AND i.indisunique AND i.indisvalid
                                        """).SingleAsync());

                                // #129: exact immutable assessment ancestry
                                // also protects ALL original source ref
                                // and object-closure hashes, bundle,
                                // Prepared marker and source-read time.
                                // A future SQL writer cannot smuggle a
                                // foreign-but-valid physical proof under
                                // the same mirror and original lease.
                                Assert.Equal(1L, await verifiedDb.Database
                                    .SqlQuery<long>($"""
                                        SELECT count(*)::bigint AS "Value"
                                        FROM pg_constraint c
                                        WHERE c.conname =
                                            'fk_f061v2e_second_review_object_scope'
                                            AND c.conrelid =
                                                'git_later_checkpoint_mirror_second_reviews'::regclass
                                            AND c.confrelid =
                                                'git_later_checkpoint_mirror_generation_assessments'::regclass
                                            AND c.contype = 'f'
                                            AND cardinality(c.conkey) = 8
                                            AND cardinality(c.confkey) = 8
                                            AND c.convalidated
                                        """).SingleAsync());
                                var objectScopeFk = await verifiedDb.Database
                                    .SqlQuery<string>($"""
                                        SELECT pg_get_constraintdef(c.oid)
                                            AS "Value"
                                        FROM pg_constraint c
                                        WHERE c.conname =
                                            'fk_f061v2e_second_review_object_scope'
                                            AND c.conrelid =
                                                'git_later_checkpoint_mirror_second_reviews'::regclass
                                        """).SingleAsync();
                                Assert.Contains("source_full_ref_proof_sha256",
                                    objectScopeFk);
                                Assert.Contains("source_ref_set_sha256",
                                    objectScopeFk);
                                Assert.Contains("prepared_intent_sha256",
                                    objectScopeFk);
                                Assert.Contains("object_set_sha256",
                                    objectScopeFk);
                                Assert.Contains("object_count",
                                    objectScopeFk);
                                Assert.Contains("source_reviewed_at_utc",
                                    objectScopeFk);
                                Assert.Equal(1L, await verifiedDb.Database
                                    .SqlQuery<long>($"""
                                        SELECT count(*)::bigint AS "Value"
                                        FROM pg_index i
                                        JOIN pg_class t ON t.oid = i.indexrelid
                                        WHERE t.relname =
                                            'uq_f061v2e_assessment_review_object_scope'
                                            AND i.indisunique AND i.indisvalid
                                        """).SingleAsync());

                                // #129: even if a future protected SQL
                                // writer is enabled, a foreign event
                                // cannot point at a matching mirror
                                // but forged reviewer, decision, evidence
                                // SHA or occurrence time. The database
                                // FK binds the complete immutable tuple,
                                // and is installed on repeat bootstraps.
                                var boundEventForeignKey = await verifiedDb
                                    .Database.SqlQuery<string>($"""
                                        SELECT pg_get_constraintdef(c.oid)
                                            AS "Value"
                                        FROM pg_constraint c
                                        WHERE c.conname =
                                            'fk_f061v2e_second_review_exact_event'
                                            AND c.conrelid =
                                                'git_later_checkpoint_mirror_second_review_events'::regclass
                                            AND c.contype = 'f'
                                        """).SingleAsync();
                                Assert.Contains(
                                    "FOREIGN KEY (mirror_id, evidence_sha256, second_reviewer_id, decision, occurred_at_utc)",
                                    boundEventForeignKey);
                                Assert.Contains(
                                    "REFERENCES git_later_checkpoint_mirror_second_reviews(mirror_id, evidence_sha256, second_reviewer_id, decision, reviewed_at_utc)",
                                    boundEventForeignKey);
                                Assert.Equal(1L, await verifiedDb.Database
                                    .SqlQuery<long>($"""
                                        SELECT count(*)::bigint AS "Value"
                                        FROM pg_index i
                                        JOIN pg_class t
                                            ON t.oid = i.indexrelid
                                        WHERE t.relname =
                                            'uq_f061v2e_second_review_event_tuple'
                                            AND i.indisunique
                                            AND i.indisvalid
                                        """).SingleAsync());

                                Assert.Equal(
                                    Npgsql.PostgresErrorCodes.RaiseException,
                                    (await Assert.ThrowsAsync<Npgsql.PostgresException>(
                                        () => verifiedDb.Database
                                            .ExecuteSqlInterpolatedAsync($"""
                                                INSERT INTO
                                                    git_later_checkpoint_mirror_second_reviews
                                                    (mirror_id)
                                                VALUES ({staging.PhysicalMirrorId})
                                                """))).SqlState);
                                Assert.Equal(
                                    Npgsql.PostgresErrorCodes.RaiseException,
                                    (await Assert.ThrowsAsync<Npgsql.PostgresException>(
                                        () => verifiedDb.Database
                                            .ExecuteSqlInterpolatedAsync($"""
                                                INSERT INTO
                                                    git_later_checkpoint_mirror_second_review_events
                                                    (mirror_id)
                                                VALUES ({staging.PhysicalMirrorId})
                                                """))).SqlState);
                                Assert.Equal(0L, await verifiedDb.Database
                                    .SqlQuery<long>($"""
                                        SELECT count(*)::bigint AS "Value"
                                        FROM git_later_checkpoint_mirror_second_reviews
                                        """).SingleAsync());
                                Assert.Equal(0L, await verifiedDb.Database
                                    .SqlQuery<long>($"""
                                        SELECT count(*)::bigint AS "Value"
                                        FROM git_later_checkpoint_mirror_second_review_events
                                        """).SingleAsync());
                                // The session PRIMARY KEY is globally
                                // unique (not scoped to one mirror);
                                // nonce and mirror each have a separate
                                // uniqueness fence. Exact FK and time
                                // windows would apply to a future
                                // privileged single-transaction writer.
                                Assert.Equal(3L, await verifiedDb.Database
                                    .SqlQuery<long>($"""
                                        SELECT count(*)::bigint AS "Value"
                                        FROM pg_constraint
                                        WHERE conrelid =
                                            'git_later_checkpoint_mirror_second_review_session_consumptions'::regclass
                                            AND contype IN ('p', 'u')
                                        """).SingleAsync());
                                Assert.Equal(1L, await verifiedDb.Database
                                    .SqlQuery<long>($"""
                                        SELECT count(*)::bigint AS "Value"
                                        FROM pg_constraint
                                        WHERE conname =
                                            'fk_f061v2e_second_review_session_exact_decision'
                                            AND conrelid =
                                                'git_later_checkpoint_mirror_second_review_session_consumptions'::regclass
                                            AND contype = 'f'
                                            AND cardinality(conkey) = 5
                                        """).SingleAsync());
                                Assert.Equal(
                                    Npgsql.PostgresErrorCodes.RaiseException,
                                    (await Assert.ThrowsAsync<Npgsql.PostgresException>(
                                        () => verifiedDb.Database
                                            .ExecuteSqlInterpolatedAsync($"""
                                                INSERT INTO
                                                    git_later_checkpoint_mirror_second_review_session_consumptions
                                                    (review_session_id)
                                                VALUES ({Guid.NewGuid()})
                                                """))).SqlState);
                                Assert.Equal(0L, await verifiedDb.Database
                                    .SqlQuery<long>($"""
                                        SELECT count(*)::bigint AS "Value"
                                        FROM git_later_checkpoint_mirror_second_review_session_consumptions
                                        """).SingleAsync());
                                await verifiedDb.Database.ExecuteSqlInterpolatedAsync($"""
                                    INSERT INTO
                                        git_later_checkpoint_mirror_generation_assessments (
                                            mirror_id, workpack_id,
                                            checkpoint_id, source_snapshot_id,
                                            evidence_version, original_worker_id,
                                            original_lease_token,
                                            original_generation,
                                            suggested_next_generation,
                                            staging_evidence_sha256,
                                            source_full_ref_proof_sha256,
                                            source_ref_set_sha256,
                                            target_receipt_sha256,
                                            bundle_sha256, bundle_bytes,
                                            isolation_receipt_sha256,
                                            recovery_decision_sha256,
                                            prepared_intent_sha256,
                                            isolation_directory_name,
                                            object_set_sha256, object_count,
                                            source_reviewed_at_utc,
                                            assessed_at_utc, evidence_sha256)
                                    VALUES (
                                        {generationAssessment.PhysicalMirrorId},
                                        {generationAssessment.WorkpackId},
                                        {generationAssessment.CheckpointId},
                                        {generationAssessment.SourceSnapshotId},
                                        {generationAssessment.EvidenceVersion},
                                        {generationAssessment.OriginalWorkerId},
                                        {generationAssessment.OriginalLeaseToken},
                                        {generationAssessment.OriginalGeneration},
                                        {generationAssessment.SuggestedNextGeneration},
                                        {generationAssessment.StagingEvidenceSha256},
                                        {generationAssessment.SourceFullRefProofSha256},
                                        {generationAssessment.SourceRefSetSha256},
                                        {generationAssessment.TargetReceiptSha256},
                                        {generationAssessment.BundleSha256},
                                        {generationAssessment.BundleBytes},
                                        {generationAssessment.IsolationReceiptSha256},
                                        {generationAssessment.RecoveryDecisionSha256},
                                        {generationAssessment.PreparedIntentSha256},
                                        {generationAssessment.IsolationDirectoryName},
                                        {generationAssessment.ObjectSetSha256},
                                        {generationAssessment.ObjectCount},
                                        {generationAssessment.SourceReviewedAtUtc},
                                        {generationAssessment.AssessedAtUtc},
                                        {generationAssessment.EvidenceSha256})
                                    """);
                                // #129: without the #127 persisted
                                // row AND its matching immutable Assessed
                                // event, a second-human GET candidate must
                                // fail closed even after a successful real
                                // POSIX-locked quarantine audit.
                                Assert.Equal(
                                    "LaterCheckpointMirrorSecondReviewAssessmentMissing",
                                    (await Assert.ThrowsAsync<
                                        GitSourceSnapshotMirrorException>(() =>
                                        postIsolation.ReadAssessedAsync(
                                            run.Id, plan.Id, later.Id,
                                            observationId, first.WorkpackId,
                                            "repo-1",
                                            targetReceipt.EvidenceSha256,
                                            candidate.RequestedBy,
                                            default))).Code);
                                await verifiedDb.Database.ExecuteSqlInterpolatedAsync($"""
                                    INSERT INTO
                                        git_later_checkpoint_mirror_generation_assessment_events (
                                            mirror_id, evidence_sha256,
                                            type, occurred_at_utc)
                                    VALUES (
                                        {generationAssessment.PhysicalMirrorId},
                                        {generationAssessment.EvidenceSha256},
                                        'Assessed',
                                        {generationAssessment.AssessedAtUtc})
                                    """);
                                // #129: the locked physical/SQL handoff
                                // MUST NOT invoke the source callback if
                                // the actual quarantined marker is forged.
                                // Restore exact bytes for subsequent audit.
                                var lockedAuditMarker = Path.Combine(
                                    physicalMirror.VerifiedMirrorRoot,
                                    isolated.IsolationDirectoryName,
                                    "agic-f061v2-reviewed-isolation-intent");
                                var lockedAuditMarkerBytes =
                                    await File.ReadAllBytesAsync(lockedAuditMarker);
                                try
                                {
                                    await File.WriteAllTextAsync(
                                        lockedAuditMarker, "foreign-intent");
                                    var callbacksOnTamper = 0;
                                    await Assert.ThrowsAsync<
                                        GitSourceSnapshotMirrorException>(() =>
                                        localSession.WithVerifiedQuarantineAsync(
                                            completedSource.Stage,
                                            completedSource.DurableBundle,
                                            completedSource.ExpectedInspection,
                                            completedSource.HumanDecision,
                                            completedSource.PreparedIntent,
                                            token =>
                                            {
                                                Interlocked.Increment(
                                                    ref callbacksOnTamper);
                                                return Task.FromResult("untrusted");
                                            }, default));
                                    Assert.Equal(0, callbacksOnTamper);
                                }
                                finally
                                {
                                    await File.WriteAllBytesAsync(
                                        lockedAuditMarker,
                                        lockedAuditMarkerBytes);
                                }
                                // #129: PostgreSQL connection A holds
                                // source-first advisory lease lock. A
                                // separate guarded reader MUST refuse
                                // non-blocking source lock acquisition
                                // before the writer can need the F-060
                                // root. This exercises the exact PG
                                // source/plan lock IDs used in production.
                                await using (var blockerDb =
                                    fixture.NewContext())
                                {
                                    await using var heldSource =
                                        await blockerDb.Database
                                            .BeginTransactionAsync();
                                    await blockerDb.Database
                                        .ExecuteSqlInterpolatedAsync(
                                            $"SELECT pg_advisory_xact_lock(hashtext({staging.SourceConnectionId.ToString()}), 732911)");
                                    using var failFastDeadline =
                                        new CancellationTokenSource(
                                            TimeSpan.FromSeconds(30));
                                    Assert.Equal(
                                        "LaterCheckpointMirrorPostIsolationSourceBusy",
                                        (await Assert.ThrowsAsync<
                                            GitSourceSnapshotMirrorException>(() =>
                                            latestReader.ReadCompletedAsync(
                                                run.Id, plan.Id, later.Id,
                                                observationId, first.WorkpackId,
                                                "repo-1",
                                                targetReceipt.EvidenceSha256,
                                                candidate.RequestedBy,
                                                failFastDeadline.Token,
                                                failIfSourceBusy: true))).Code);
                                    // The internal 12s reader budget,
                                    // never this outer 30s caller token,
                                    // must terminate source-lock contention.
                                    Assert.False(failFastDeadline
                                        .IsCancellationRequested);
                                    await heldSource.RollbackAsync();
                                }
                                // #129: trusted completed #127
                                // assessment is now joined to its real
                                // Assessed event and domain SHA, then
                                // rechecked against live source/target
                                // SQL and dual-fsck physical quarantine.
                                var assessedIsolation =
                                    await postIsolation.ReadAssessedAsync(
                                        run.Id, plan.Id, later.Id,
                                        observationId, first.WorkpackId,
                                        "repo-1",
                                        targetReceipt.EvidenceSha256,
                                        candidate.RequestedBy, default,
                                        authenticatedSecondReviewerId:
                                            secondReviewer);
                                Assert.Equal(generationAssessment.EvidenceSha256,
                                    assessedIsolation.AssessmentEvidenceSha256);
                                // Actor-scoped, domain-separated SHA1/SHA256
                                // PostgreSQL/Git positive and denial
                                // checks; no SQL decision is persisted.
                                Assert.True(GitSourceBundleStorageCheck.ValidSha(
                                    assessedIsolation.ApprovedPreviewEvidenceSha256 ?? ""));
                                Assert.True(GitSourceBundleStorageCheck.ValidSha(
                                    assessedIsolation.RejectedPreviewEvidenceSha256 ?? ""));
                                Assert.NotEqual(
                                    assessedIsolation.ApprovedPreviewEvidenceSha256,
                                    assessedIsolation.RejectedPreviewEvidenceSha256);
                                Assert.NotNull(assessedIsolation.PreviewReviewedAtUtc);
                                Assert.False(assessedIsolation.Inspection
                                    .DurableSecondReviewPersisted);
                                Assert.Equal(
                                    generationAssessment.SuggestedNextGeneration,
                                    assessedIsolation.SuggestedNextGeneration);
                                Assert.Equal(generationAssessment.AssessedAtUtc,
                                    assessedIsolation.AssessedAtUtc);
                                // A submitted fixed GET timestamp must
                                // re-seal to the *same* actor-bound SHA,
                                // after repeating all live source/SQL
                                // and POSIX physical checks. It remains
                                // non-authorizing, and no nonce is
                                // actually consumed in this preflight.
                                var fixedPreview =
                                    await postIsolation.ReadAssessedAsync(
                                        run.Id, plan.Id, later.Id,
                                        observationId, first.WorkpackId,
                                        "repo-1",
                                        targetReceipt.EvidenceSha256,
                                        candidate.RequestedBy, default,
                                        authenticatedSecondReviewerId:
                                            secondReviewer,
                                        requestedReviewAtUtc:
                                            assessedIsolation.PreviewReviewedAtUtc,
                                        authenticatedReviewSessionId:
                                            Guid.NewGuid(),
                                        decisionNonce: Guid.NewGuid());
                                Assert.Equal(
                                    assessedIsolation.ApprovedPreviewEvidenceSha256,
                                    fixedPreview.ApprovedPreviewEvidenceSha256);
                                Assert.Equal(
                                    assessedIsolation.RejectedPreviewEvidenceSha256,
                                    fixedPreview.RejectedPreviewEvidenceSha256);
                                Assert.False(fixedPreview.Inspection
                                    .DurableSecondReviewPersisted);
                                Assert.Equal(
                                    "LaterCheckpointMirrorSecondReviewPreflightExpired",
                                    (await Assert.ThrowsAsync<
                                        GitSourceSnapshotMirrorException>(() =>
                                            postIsolation.ReadAssessedAsync(
                                                run.Id, plan.Id, later.Id,
                                                observationId, first.WorkpackId,
                                                "repo-1",
                                                targetReceipt.EvidenceSha256,
                                                candidate.RequestedBy, default,
                                                authenticatedSecondReviewerId:
                                                    secondReviewer,
                                                requestedReviewAtUtc:
                                                    assessedIsolation.PreviewReviewedAtUtc
                                                        ?.AddMinutes(-3),
                                                authenticatedReviewSessionId:
                                                    Guid.NewGuid(),
                                                decisionNonce:
                                                    Guid.NewGuid()))).Code);

                                Assert.False(
                                    assessedIsolation.Inspection.CanReAdmit ||
                                    assessedIsolation.Inspection.CanPush ||
                                    assessedIsolation.Inspection.MigrationComplete);
                                Assert.Equal(1L, await verifiedDb.Database
                                    .SqlQuery<long>($"""
                                        SELECT count(*)::bigint AS "Value"
                                        FROM git_later_checkpoint_mirror_generation_assessments
                                        WHERE mirror_id = {staging.PhysicalMirrorId}
                                            AND original_worker_id = {diagnostic.WorkerId}
                                            AND original_lease_token = {diagnostic.LeaseToken}
                                            AND original_generation = {diagnostic.LeaseGeneration}
                                            AND suggested_next_generation =
                                                {diagnostic.LeaseGeneration + 1}
                                            AND isolation_receipt_sha256 =
                                                {isolated.EvidenceSha256}
                                            AND recovery_decision_sha256 =
                                                {isolated.RecoveryDecisionSha256}
                                            AND prepared_intent_sha256 =
                                                {isolated.PreparedIntentSha256}
                                            AND isolation_directory_name =
                                                {isolated.IsolationDirectoryName}
                                            AND object_set_sha256 =
                                                {isolated.ObjectSetSha256}
                                            AND object_count =
                                                {isolated.ObjectCount}
                                            AND source_reviewed_at_utc =
                                                {generationSourceRead.ReviewedAtUtc}
                                            AND NOT durable_readmission_approved
                                            AND NOT can_readmit
                                            AND NOT can_materialize
                                            AND NOT can_push AND NOT can_retry
                                            AND NOT can_dispatch
                                            AND NOT migration_complete
                                        """).SingleAsync());
                                Assert.Equal(1L, await verifiedDb.Database
                                    .SqlQuery<long>($"""
                                        SELECT count(*)::bigint AS "Value"
                                        FROM git_later_checkpoint_mirror_generation_assessment_events
                                        WHERE mirror_id = {staging.PhysicalMirrorId}
                                            AND evidence_sha256 =
                                                {generationAssessment.EvidenceSha256}
                                            AND type = 'Assessed'
                                        """).SingleAsync());
                                // A replay with a foreign Prepared SHA is
                                // rejected by the INSERT trigger before
                                // any uniqueness/append-only transition.
                                Assert.Equal(
                                    Npgsql.PostgresErrorCodes.RaiseException,
                                    (await Assert.ThrowsAsync<
                                        Npgsql.PostgresException>(() =>
                                        verifiedDb.Database.ExecuteSqlInterpolatedAsync($"""
                                            INSERT INTO
                                                git_later_checkpoint_mirror_generation_assessments (
                                                    mirror_id, workpack_id,
                                                    checkpoint_id, source_snapshot_id,
                                                    evidence_version, original_worker_id,
                                                    original_lease_token,
                                                    original_generation,
                                                    suggested_next_generation,
                                                    staging_evidence_sha256,
                                                    source_full_ref_proof_sha256,
                                                    source_ref_set_sha256,
                                                    target_receipt_sha256,
                                                    bundle_sha256, bundle_bytes,
                                                    isolation_receipt_sha256,
                                                    recovery_decision_sha256,
                                                    prepared_intent_sha256,
                                                    isolation_directory_name,
                                                    object_set_sha256, object_count,
                                                    source_reviewed_at_utc,
                                                    assessed_at_utc, evidence_sha256)
                                            VALUES (
                                                {generationAssessment.PhysicalMirrorId},
                                                {generationAssessment.WorkpackId},
                                                {generationAssessment.CheckpointId},
                                                {generationAssessment.SourceSnapshotId},
                                                {generationAssessment.EvidenceVersion},
                                                {generationAssessment.OriginalWorkerId},
                                                {generationAssessment.OriginalLeaseToken},
                                                {generationAssessment.OriginalGeneration},
                                                {generationAssessment.SuggestedNextGeneration},
                                                {generationAssessment.StagingEvidenceSha256},
                                                {generationAssessment.SourceFullRefProofSha256},
                                                {generationAssessment.SourceRefSetSha256},
                                                {generationAssessment.TargetReceiptSha256},
                                                {generationAssessment.BundleSha256},
                                                {generationAssessment.BundleBytes},
                                                {generationAssessment.IsolationReceiptSha256},
                                                {generationAssessment.RecoveryDecisionSha256},
                                                {new string('0',64)},
                                                {generationAssessment.IsolationDirectoryName},
                                                {generationAssessment.ObjectSetSha256},
                                                {generationAssessment.ObjectCount},
                                                {generationAssessment.SourceReviewedAtUtc},
                                                {generationAssessment.AssessedAtUtc},
                                                {generationAssessment.EvidenceSha256})
                                            """))).SqlState);
                                Assert.Equal(
                                    Npgsql.PostgresErrorCodes.RaiseException,
                                    (await Assert.ThrowsAsync<
                                        Npgsql.PostgresException>(() =>
                                        verifiedDb.Database.ExecuteSqlInterpolatedAsync($"""
                                            UPDATE git_later_checkpoint_mirror_generation_assessments
                                            SET can_readmit = true
                                            WHERE mirror_id = {staging.PhysicalMirrorId}
                                            """))).SqlState);
                                Assert.Equal(
                                    Npgsql.PostgresErrorCodes.RaiseException,
                                    (await Assert.ThrowsAsync<
                                        Npgsql.PostgresException>(() =>
                                        verifiedDb.Database.ExecuteSqlInterpolatedAsync($"""
                                            DELETE FROM git_later_checkpoint_mirror_generation_assessment_events
                                            WHERE mirror_id = {staging.PhysicalMirrorId}
                                            """))).SqlState);
                                // The original generation is still terminal,
                                // and no fresh Held or Verified claim exists.
                                Assert.Equal("RecoveryRequired",
                                    await verifiedDb.Database.SqlQuery<string>($"""
                                        SELECT state AS "Value"
                                        FROM git_later_checkpoint_mirror_leases
                                        WHERE mirror_id = {staging.PhysicalMirrorId}
                                        """).SingleAsync());
                                Assert.Equal(
                                    "LaterCheckpointMirrorIsolationReconciliationAlreadyRecorded",
                                    (await Assert.ThrowsAsync<
                                        GitSourceSnapshotMirrorException>(() =>
                                        isolation.RunAsync(run.Id, plan.Id,
                                            later.Id, observationId,
                                            first.WorkpackId, "repo-1",
                                            targetReceipt.EvidenceSha256,
                                            candidate.RequestedBy,
                                            default))).Code);
                                Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException,
                                    (await Assert.ThrowsAsync<
                                        Npgsql.PostgresException>(() =>
                                        verifiedDb.Database.ExecuteSqlInterpolatedAsync($"""
                                            UPDATE git_later_checkpoint_mirror_isolation_receipts
                                            SET can_push = true
                                            WHERE mirror_id = {staging.PhysicalMirrorId}
                                            """))).SqlState);
                                Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException,
                                    (await Assert.ThrowsAsync<
                                        Npgsql.PostgresException>(() =>
                                        verifiedDb.Database.ExecuteSqlInterpolatedAsync($"""
                                            DELETE FROM git_later_checkpoint_mirror_isolation_receipt_events
                                            WHERE mirror_id = {staging.PhysicalMirrorId}
                                            """))).SqlState);
                                Assert.Equal("RecoveryRequired",
                                    await verifiedDb.Database.SqlQuery<string>($"""
                                        SELECT state AS "Value"
                                        FROM git_later_checkpoint_mirror_leases
                                        WHERE mirror_id = {staging.PhysicalMirrorId}
                                        """).SingleAsync());
                                Assert.Equal(0L, await verifiedDb.Database
                                    .SqlQuery<long>($"""
                                        SELECT count(*)::bigint AS "Value"
                                        FROM git_later_checkpoint_mirror_verified_evidence
                                        WHERE mirror_id = {staging.PhysicalMirrorId}
                                        """).SingleAsync());
                            }
                        }
                        else
                        {
                        var actual = await mirrorWorkerRunner.RunAsync(
                            run.Id, plan.Id, later.Id, observationId,
                            first.WorkpackId, "repo-1",
                            targetReceipt.EvidenceSha256,
                            candidate.RequestedBy, activeWorker, default);
                        Assert.True(actual.DurableEvidencePersisted);
                        Assert.Equal(activeWorker, actual.WorkerId);
                        Assert.Equal(staging.PhysicalMirrorId,
                            actual.PhysicalMirrorId);
                        Assert.Equal(staging.SourceSnapshotId,
                            actual.SourceSnapshotId);
                        Assert.Equal(committed.BundleSha256,
                            actual.BundleSha256);
                        Assert.Equal(staging.SourceRefSetSha256,
                            actual.SourceRefSetSha256);
                        Assert.Equal(1L, actual.LeaseGeneration);
                        Assert.False(actual.CanMaterialize ||
                            actual.CanPush || actual.CanRetry ||
                            actual.CanDispatch || actual.MigrationComplete);
                        var recorded = await verifiedDb.Database
                            .SqlQuery<long>($"""
                                SELECT count(*)::bigint AS "Value"
                                FROM git_later_checkpoint_mirror_verified_evidence
                                WHERE mirror_id = {actual.PhysicalMirrorId}
                                    AND worker_id = {activeWorker}
                                    AND evidence_sha256 = {actual.EvidenceSha256}
                                    AND NOT can_push AND NOT can_retry
                                    AND NOT can_dispatch
                                    AND NOT migration_complete
                                """).SingleAsync();
                        Assert.Equal(1L, recorded);
                        var mirrorLeaseEvents = await verifiedDb.Database
                            .SqlQuery<string>($"""
                                SELECT type AS "Value"
                                FROM git_later_checkpoint_mirror_lease_events
                                WHERE mirror_id = {actual.PhysicalMirrorId}
                                """).ToArrayAsync();
                        Assert.Contains("Acquired", mirrorLeaseEvents);
                        // Before the real Git operation we waited for two
                        // committed SQL pulses from independent contexts.
                        // Any additional post-import renewal is separate:
                        // this assertion still proves that heartbeat
                        // progress happened WHILE local work was pending.
                        Assert.True(mirrorLeaseEvents.Count(x => x == "Renewed") >= 2);
                        Assert.Equal(1, mirrorLeaseEvents.Count(x => x == "Verified"));
                        Assert.DoesNotContain("Quarantined", mirrorLeaseEvents);
                        var secondWorkerDenied = await Assert.ThrowsAsync<
                            GitSourceSnapshotMirrorException>(() =>
                            mirrorWorkerRunner.RunAsync(run.Id, plan.Id, later.Id,
                                observationId, first.WorkpackId, "repo-1",
                                targetReceipt.EvidenceSha256,
                                candidate.RequestedBy, Guid.NewGuid(),
                                default));
                        Assert.Equal("LaterCheckpointMirrorLeaseAlreadyVerified",
                            secondWorkerDenied.Code);
                        var recoveryInspector = auditScope.ServiceProvider
                            .GetRequiredService<
                                PostgresGitIncrementalLaterCheckpointMirrorRecoveryInspector>();
                        Assert.Equal(
                            "LaterCheckpointMirrorRecoveryNotQuarantined",
                            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                                () => recoveryInspector.ReadAsync(
                                    run.Id, plan.Id, later.Id, observationId,
                                    first.WorkpackId, "repo-1",
                                    targetReceipt.EvidenceSha256,
                                    candidate.RequestedBy, default))).Code);
                        Assert.Empty(await verifiedDb.GitSourceMirrorLifecycles
                            .AsNoTracking().Where(x =>
                                x.MirrorId == actual.PhysicalMirrorId)
                            .ToArrayAsync());
                        }
                    }
                    else
                    {
                        // #118: actual internal F-061v2 ClaimAsync uses the
                        // latest F-091/F-097b/F-031/F-035v2 and DB-clock
                        // source-first locks. It remains non-authorizing:
                        // no local import, Verified or GitHub transport.
                        var leaseStore = auditScope.ServiceProvider
                            .GetRequiredService<
                                PostgresGitIncrementalLaterCheckpointMirrorLeaseStore>();
                        var worker = Guid.NewGuid();
                        var acquiredLease = await leaseStore.ClaimAsync(
                            run.Id, plan.Id, later.Id, observationId,
                            first.WorkpackId, "repo-1",
                            targetReceipt.EvidenceSha256,
                            candidate.RequestedBy, worker,
                            TimeSpan.FromSeconds(90), default);
                        Assert.Equal(checkedAdmission.PhysicalMirrorId,
                            acquiredLease.MirrorId);
                        Assert.Equal(first.WorkpackId, acquiredLease.WorkpackId);
                        Assert.Equal(1, acquiredLease.Generation);
                        Assert.Equal(worker, acquiredLease.WorkerId);
                        Assert.False(acquiredLease.CanMaterialize ||
                            acquiredLease.CanPush || acquiredLease.CanRetry ||
                            acquiredLease.CanDispatch ||
                            acquiredLease.MigrationComplete);
                        Assert.Equal("LaterCheckpointMirrorLeaseBusy",
                            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                                () => leaseStore.ClaimAsync(
                                    run.Id, plan.Id, later.Id, observationId,
                                    first.WorkpackId, "repo-1",
                                    targetReceipt.EvidenceSha256,
                                    candidate.RequestedBy, Guid.NewGuid(),
                                    TimeSpan.FromSeconds(90), default))).Code);
                        Assert.Equal("LaterCheckpointMirrorLeaseFenced",
                            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                                () => leaseStore.RenewAsync(
                                    acquiredLease with {
                                        WorkerId = Guid.NewGuid()
                                    }, TimeSpan.FromSeconds(90),
                                    default))).Code);
                        Assert.Equal("LaterCheckpointMirrorLeaseNotExtended",
                            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                                () => leaseStore.RenewAsync(
                                    acquiredLease, TimeSpan.FromSeconds(30),
                                    default))).Code);
                        var renewed = await leaseStore.RenewAsync(
                            acquiredLease, TimeSpan.FromSeconds(90), default);
                        Assert.Equal(acquiredLease.Generation, renewed.Generation);
                        Assert.Equal(acquiredLease.LeaseToken, renewed.LeaseToken);
                        Assert.True(renewed.ExpiresAtUtc >=
                            acquiredLease.ExpiresAtUtc);
                        Assert.Equal("LaterCheckpointMirrorLeaseFenced",
                            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                                () => leaseStore.RenewAsync(acquiredLease,
                                    TimeSpan.FromSeconds(90), default))).Code);
                        // Cross-context restart/replay: lease owner identity
                        // and last deadline must survive a new DbContext.
                        await using (var restarted = api.Host.Services
                            .CreateAsyncScope())
                        {
                            var rehydrated = restarted.ServiceProvider
                                .GetRequiredService<
                                    PostgresGitIncrementalLaterCheckpointMirrorLeaseStore>();
                            renewed = await rehydrated.RenewAsync(
                                renewed, TimeSpan.FromSeconds(90), default);
                        }
                        Assert.Equal(worker, renewed.WorkerId);
                        // Independent DbContext/connection competitors must
                        // serialize on the source-first PostgreSQL locks.
                        var competitors = await Task.WhenAll(
                            Enumerable.Range(0, 3).Select(async _ =>
                            {
                                await using var replica = api.Host.Services
                                    .CreateAsyncScope();
                                var contender = replica.ServiceProvider
                                    .GetRequiredService<
                                        PostgresGitIncrementalLaterCheckpointMirrorLeaseStore>();
                                var rejected = await Assert.ThrowsAsync<
                                    GitSourceSnapshotMirrorException>(() =>
                                    contender.ClaimAsync(
                                        run.Id, plan.Id, later.Id, observationId,
                                        first.WorkpackId, "repo-1",
                                        targetReceipt.EvidenceSha256,
                                        candidate.RequestedBy, Guid.NewGuid(),
                                        TimeSpan.FromSeconds(90), default));
                                return rejected.Code;
                            }));
                        Assert.All(competitors, code =>
                            Assert.Equal("LaterCheckpointMirrorLeaseBusy", code));
                        var auditTypes = await verifiedDb.Database
                            .SqlQuery<string>($"""
                                SELECT type AS "Value"
                                FROM git_later_checkpoint_mirror_lease_events
                                WHERE mirror_id = {checkedAdmission.PhysicalMirrorId}
                                """).ToArrayAsync();
                        Assert.Contains("Acquired", auditTypes);
                        Assert.Equal(2, auditTypes.Count(t => t == "Renewed"));
                        Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation,
                            (await Assert.ThrowsAsync<Npgsql.PostgresException>(
                                () => verifiedDb.Database.ExecuteSqlInterpolatedAsync($"""
                                    UPDATE git_later_checkpoint_mirror_leases
                                    SET can_push = {true}
                                    WHERE mirror_id =
                                        {checkedAdmission.PhysicalMirrorId}
                                    """))).SqlState);
                        Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException,
                            (await Assert.ThrowsAsync<Npgsql.PostgresException>(
                                () => verifiedDb.Database.ExecuteSqlInterpolatedAsync($"""
                                    UPDATE git_later_checkpoint_mirror_leases
                                    SET bundle_sha256 = {new string('f', 64)}
                                    WHERE mirror_id =
                                        {checkedAdmission.PhysicalMirrorId}
                                    """))).SqlState);
                        Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException,
                            (await Assert.ThrowsAsync<Npgsql.PostgresException>(
                                () => verifiedDb.Database.ExecuteSqlInterpolatedAsync($"""
                                    UPDATE git_later_checkpoint_mirror_lease_events
                                    SET type = {"Verified"}
                                    WHERE mirror_id =
                                        {checkedAdmission.PhysicalMirrorId}
                                    """))).SqlState);
                        if (verifyPhysical)
                        {
                            // #119: two genuine SHA-1/SHA-256 source epochs
                            // and sealed v2 F-035 bytes. Materialize only
                            // the trusted local UUID.git, THEN independently
                            // double-fsck and rehash it inside the durable
                            // source-first F-061v2 Verified journal.
                            var actualImporter =
                                new LocalGitSourceSnapshotMirrorMaterializer(
                                    physicalMirror!.BundleRoot,
                                    physicalMirror.VerifiedMirrorRoot,
                                    new LocalGitBundleArtifactStore(
                                        physicalMirror.BundleRoot),
                                    laterCheckpointEnabled: true);
                            var imported = await actualImporter
                                .MaterializeLaterCheckpointAsync(
                                    staging, committed, default);
                            Assert.Equal(staging.PhysicalMirrorId,
                                imported.PhysicalMirrorId);
                            Assert.Equal(staging.SourceRefSetSha256,
                                imported.SourceRefSetSha256);
                            Assert.False(imported.DurableEvidencePersisted ||
                                imported.CanMaterialize || imported.CanPush ||
                                imported.CanRetry || imported.CanDispatch ||
                                imported.MigrationComplete);
                            var verifiedWriter = auditScope.ServiceProvider
                                .GetRequiredService<
                                    PostgresGitIncrementalLaterCheckpointMirrorVerifiedJournal>();
                            // Even an independently sealed SHA-only receipt
                            // cannot bypass PostgreSQL F-061v2: direct SQL with
                            // a FOREIGN WORKER must be rejected by the lease
                            // scope trigger while the genuine lease is Held.
                            var foreignProof =
                                GitIncrementalLaterCheckpointMirrorVerifiedIntegrity.Seal(
                                    staging, committed, imported, Guid.NewGuid(),
                                    renewed.LeaseToken, renewed.Generation,
                                    DateTimeOffset.UtcNow);
                            Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException,
                                (await Assert.ThrowsAsync<Npgsql.PostgresException>(
                                    () => verifiedDb.Database.ExecuteSqlInterpolatedAsync($"""
                                        INSERT INTO git_later_checkpoint_mirror_verified_evidence (
                                            mirror_id, workpack_id, evidence_version,
                                            preparation_id, plan_id, source_snapshot_id,
                                            checkpoint_id, target_observation_id,
                                            worker_id, lease_token, lease_generation,
                                            staging_evidence_sha256,
                                            full_ref_proof_sha256,
                                            bundle_evidence_sha256, bundle_sha256,
                                            source_ref_set_sha256, git_object_format,
                                            object_set_sha256, object_count,
                                            physical_verified_at_utc, recorded_at_utc,
                                            evidence_sha256)
                                        VALUES ({foreignProof.PhysicalMirrorId},
                                            {foreignProof.WorkpackId},
                                            {foreignProof.EvidenceVersion},
                                            {foreignProof.PreparationId},
                                            {foreignProof.PlanId},
                                            {foreignProof.SourceSnapshotId},
                                            {foreignProof.CheckpointId},
                                            {foreignProof.TargetObservationId},
                                            {foreignProof.WorkerId},
                                            {foreignProof.LeaseToken},
                                            {foreignProof.LeaseGeneration},
                                            {foreignProof.StagingEvidenceSha256},
                                            {foreignProof.FullRefProofSha256},
                                            {foreignProof.BundleEvidenceSha256},
                                            {foreignProof.BundleSha256},
                                            {foreignProof.SourceRefSetSha256},
                                            {foreignProof.GitObjectFormat},
                                            {foreignProof.ObjectSetSha256},
                                            {foreignProof.ObjectCount},
                                            {foreignProof.PhysicalVerifiedAtUtc},
                                            {foreignProof.RecordedAtUtc},
                                            {foreignProof.EvidenceSha256})
                                        """))).SqlState);

                            if (corruptPhysical)
                            {
                                // Corrupt a real imported source Git ref after
                                // the local bundle was checked, without changing
                                // F-035v2. A SHA-only proof would still look
                                // valid; a trusted fresh Git fsck MUST refuse.
                                var refFile = Path.Combine(
                                    physicalMirror.VerifiedMirrorRoot,
                                    staging.PhysicalMirrorId.ToString("N") + ".git",
                                    "refs", "heads", "main");
                                Directory.CreateDirectory(
                                    Path.GetDirectoryName(refFile)!);
                                File.WriteAllText(refFile,
                                    new string('0', oidLength) + "\n");
                                var denied = await Assert.ThrowsAsync<
                                    GitSourceSnapshotMirrorException>(() =>
                                    verifiedWriter.CompleteAsync(
                                        renewed, run.Id, plan.Id, observationId,
                                        "repo-1", targetReceipt.EvidenceSha256,
                                        default));
                                Assert.Contains("LaterCheckpointMirrorOffline",
                                    denied.Code);
                                var quarantineState = await verifiedDb.Database
                                    .SqlQuery<string>($"""
                                        SELECT state AS "Value"
                                        FROM git_later_checkpoint_mirror_leases
                                        WHERE mirror_id = {staging.PhysicalMirrorId}
                                        """).SingleAsync();
                                Assert.Equal("RecoveryRequired", quarantineState);
                                var quarantineCount = await verifiedDb.Database
                                    .SqlQuery<long>($"""
                                        SELECT count(*)::bigint AS "Value"
                                        FROM git_later_checkpoint_mirror_lease_events
                                        WHERE mirror_id = {staging.PhysicalMirrorId}
                                            AND type = 'Quarantined'
                                        """).SingleAsync();
                                Assert.Equal(1L, quarantineCount);
                                Assert.Equal(0L, await verifiedDb.Database
                                    .SqlQuery<long>($"""
                                        SELECT count(*)::bigint AS "Value"
                                        FROM git_later_checkpoint_mirror_verified_evidence
                                        WHERE mirror_id = {staging.PhysicalMirrorId}
                                        """).SingleAsync());
                                Assert.False(await leaseStore.QuarantineAsync(
                                    renewed, "PhysicalAuditFailed", default));
                                Assert.Equal("LaterCheckpointMirrorLeaseFenced",
                                    (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                                        () => leaseStore.QuarantineAsync(
                                            renewed with {
                                                WorkerId = Guid.NewGuid()
                                            }, "PhysicalAuditFailed", default))).Code);
                                Assert.Equal("LaterCheckpointMirrorVerifiedLeaseFenced",
                                    (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                                        () => verifiedWriter.CompleteAsync(
                                            renewed, run.Id, plan.Id, observationId,
                                            "repo-1", targetReceipt.EvidenceSha256,
                                            default))).Code);
                            }
                            else
                            {
                                Assert.Equal("LaterCheckpointMirrorVerifiedLeaseFenced",
                                    (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                                        () => verifiedWriter.CompleteAsync(
                                            renewed with { WorkerId = Guid.NewGuid() },
                                            run.Id, plan.Id, observationId,
                                            "repo-1", targetReceipt.EvidenceSha256,
                                            default))).Code);
                                var committedMirror = await verifiedWriter.CompleteAsync(
                                    renewed, run.Id, plan.Id, observationId,
                                    "repo-1", targetReceipt.EvidenceSha256, default);
                                Assert.Equal(1, committedMirror.EvidenceVersion);
                                Assert.Equal(renewed.LeaseToken, committedMirror.LeaseToken);
                                Assert.Equal(renewed.Generation,
                                    committedMirror.LeaseGeneration);
                                Assert.Equal(worker, committedMirror.WorkerId);
                                Assert.Equal(staging.EvidenceSha256,
                                    committedMirror.StagingEvidenceSha256);
                                Assert.Equal(fullRefs.SnapshotId,
                                    committedMirror.SourceSnapshotId);
                                Assert.Equal(staging.PhysicalMirrorId,
                                    committedMirror.PhysicalMirrorId);
                                Assert.Equal(committed.BundleSha256,
                                    committedMirror.BundleSha256);
                                Assert.Equal(imported.ObjectSetSha256,
                                    committedMirror.ObjectSetSha256);
                                Assert.Equal(imported.ObjectCount,
                                    committedMirror.ObjectCount);
                                Assert.True(committedMirror.DurableEvidencePersisted);
                                Assert.False(committedMirror.CanMaterialize ||
                                    committedMirror.CanPush || committedMirror.CanRetry ||
                                    committedMirror.CanDispatch ||
                                    committedMirror.MigrationComplete);
                                var state = await verifiedDb.Database
                                    .SqlQuery<string>($"""
                                        SELECT state AS "Value"
                                        FROM git_later_checkpoint_mirror_leases
                                        WHERE mirror_id = {staging.PhysicalMirrorId}
                                        """).SingleAsync();
                                Assert.Equal("Verified", state);
                                var written = await verifiedDb.Database.SqlQuery<long>($"""
                                    SELECT count(*)::bigint AS "Value"
                                    FROM git_later_checkpoint_mirror_verified_evidence
                                    WHERE mirror_id = {staging.PhysicalMirrorId}
                                        AND evidence_sha256 = {committedMirror.EvidenceSha256}
                                        AND lease_token = {renewed.LeaseToken}
                                        AND lease_generation = {renewed.Generation}
                                        AND worker_id = {worker}
                                        AND durable_evidence_persisted
                                        AND NOT can_materialize AND NOT can_push
                                        AND NOT can_retry AND NOT can_dispatch
                                        AND NOT migration_complete
                                    """).SingleAsync();
                                Assert.Equal(1L, written);
                                var savedEvent = await verifiedDb.Database.SqlQuery<long>($"""
                                    SELECT count(*)::bigint AS "Value"
                                    FROM git_later_checkpoint_mirror_lease_events
                                    WHERE mirror_id = {staging.PhysicalMirrorId}
                                        AND generation = {renewed.Generation}
                                        AND worker_id = {worker}
                                        AND type = 'Verified'
                                    """).SingleAsync();
                                Assert.Equal(1L, savedEvent);
                                Assert.Equal("LaterCheckpointMirrorVerifiedLeaseFenced",
                                    (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                                        () => verifiedWriter.CompleteAsync(
                                            renewed, run.Id, plan.Id, observationId,
                                            "repo-1", targetReceipt.EvidenceSha256,
                                            default))).Code);
                                Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException,
                                    (await Assert.ThrowsAsync<Npgsql.PostgresException>(
                                        () => verifiedDb.Database.ExecuteSqlInterpolatedAsync($"""
                                            UPDATE git_later_checkpoint_mirror_verified_evidence
                                            SET can_push = {true}
                                            WHERE mirror_id = {staging.PhysicalMirrorId}
                                            """))).SqlState);
                                Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException,
                                    (await Assert.ThrowsAsync<Npgsql.PostgresException>(
                                        () => verifiedDb.Database.ExecuteSqlInterpolatedAsync($"""
                                            DELETE FROM git_later_checkpoint_mirror_verified_evidence
                                            WHERE mirror_id = {staging.PhysicalMirrorId}
                                            """))).SqlState);
                                // Terminal Verified can never be reclaimed/released
                                // by an unrelated worker, nor sent to GitHub.
                                Assert.Equal("LaterCheckpointMirrorLeaseAlreadyVerified",
                                    (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                                        () => leaseStore.ClaimAsync(
                                            run.Id, plan.Id, later.Id, observationId,
                                            first.WorkpackId, "repo-1",
                                            targetReceipt.EvidenceSha256,
                                            candidate.RequestedBy, Guid.NewGuid(),
                                            TimeSpan.FromSeconds(90), default))).Code);
                            }
                        }
                        else
                        {
                            // Simulate an expired, possibly crashed F-061v2
                            // worker without sleeping or claiming it is verified.
                            // The SQL invariant still requires expiry > acquired;
                            // only the special test fixture moves it just into
                            // the past. Runtime never adjusts an active lease.
                            await verifiedDb.Database.ExecuteSqlInterpolatedAsync($"""
                                UPDATE git_later_checkpoint_mirror_leases
                                SET lease_until_utc =
                                    acquired_at_utc + interval '1 millisecond',
                                    updated_at_utc = clock_timestamp()
                                WHERE mirror_id = {checkedAdmission.PhysicalMirrorId}
                                """);
                            Assert.Equal("LaterCheckpointMirrorLeaseRecoveryRequired",
                                (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                                    () => leaseStore.ClaimAsync(
                                        run.Id, plan.Id, later.Id, observationId,
                                        first.WorkpackId, "repo-1",
                                        targetReceipt.EvidenceSha256,
                                        candidate.RequestedBy, Guid.NewGuid(),
                                        TimeSpan.FromSeconds(90), default))).Code);
                            var quarantineState = await verifiedDb.Database
                                .SqlQuery<string>($"""
                                    SELECT state AS "Value"
                                    FROM git_later_checkpoint_mirror_leases
                                    WHERE mirror_id = {checkedAdmission.PhysicalMirrorId}
                                    """).SingleAsync();
                            Assert.Equal("RecoveryRequired", quarantineState);
                            Assert.Equal("LaterCheckpointMirrorLeaseFenced",
                                (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                                    () => leaseStore.RenewAsync(
                                        renewed, TimeSpan.FromSeconds(90),
                                        default))).Code);
                            Assert.Equal("LaterCheckpointMirrorLeaseRecoveryRequired",
                                (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                                    () => leaseStore.ClaimAsync(
                                        run.Id, plan.Id, later.Id, observationId,
                                        first.WorkpackId, "repo-1",
                                        targetReceipt.EvidenceSha256,
                                        candidate.RequestedBy, Guid.NewGuid(),
                                        TimeSpan.FromSeconds(90), default))).Code);
                            var quarantines = await verifiedDb.Database
                                .SqlQuery<long>($"""
                                    SELECT count(*)::bigint AS "Value"
                                    FROM git_later_checkpoint_mirror_lease_events
                                    WHERE mirror_id = {checkedAdmission.PhysicalMirrorId}
                                        AND type = 'Quarantined'
                                    """).SingleAsync();
                            Assert.Equal(1L, quarantines);
                            // No durable Verified state or GitHub destination
                            // side effect can ever result from a crashed lease.
                            Assert.Empty(await verifiedDb.GitSourceMirrorLifecycles
                                .AsNoTracking().Where(x =>
                                    x.MirrorId == checkedAdmission.PhysicalMirrorId)
                                .ToArrayAsync());
                        }
                    }

                    Assert.Equal("LaterCheckpointMirrorStagingProvenanceUnverified",
                        (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                            () => admission.ReadAsync(
                                run.Id, plan.Id, later.Id, observationId,
                                first.WorkpackId, "repo-1",
                                targetReceipt.EvidenceSha256,
                                Guid.NewGuid(), default))).Code);
                    Assert.Equal("LaterCheckpointMirrorStagingProvenanceUnverified",
                        (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                            () => admission.ReadAsync(
                                run.Id, plan.Id, later.Id, observationId,
                                first.WorkpackId, "foreign-repo",
                                targetReceipt.EvidenceSha256,
                                candidate.RequestedBy, default))).Code);
                    Assert.Equal("LaterCheckpointMirrorAdmissionInvalidScope",
                        (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                            () => admission.ReadAsync(
                                run.Id, plan.Id, later.Id, observationId,
                                first.WorkpackId, "repo-1", "invalid",
                                candidate.RequestedBy, default))).Code);

                    Assert.Equal("LaterCheckpointMirrorStagingProvenanceUnverified",
                        (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                            () => mirrorReader.ReadAsync(run.Id, plan.Id,
                                later.Id, observationId, first.WorkpackId,
                                "repo-1", new string('0', 64),
                                candidate.RequestedBy, default))).Code);
                    Assert.Equal("LaterCheckpointMirrorStagingProvenanceUnverified",
                        (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                            () => mirrorReader.ReadAsync(run.Id, plan.Id,
                                later.Id, observationId, first.WorkpackId,
                                "repo-1", targetReceipt.EvidenceSha256,
                                Guid.NewGuid(), default))).Code);
                    Assert.Equal("LaterCheckpointMirrorStagingProvenanceUnverified",
                        (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                            () => mirrorReader.ReadAsync(run.Id, plan.Id,
                                later.Id, observationId, first.WorkpackId,
                                "foreign-repo", targetReceipt.EvidenceSha256,
                                candidate.RequestedBy, default))).Code);

                    Assert.Equal("LaterCheckpointMirrorStagingInvalid",
                        Assert.Throws<GitSourceSnapshotMirrorException>(() =>
                            GitIncrementalLaterCheckpointMirrorStagingIntegrity
                                .Verify(staging with { F061LeaseGeneration = 1 },
                                    latestPlan, joined, fullRefs,
                                    bundlePreparation, committed, first)).Code);
                    Assert.Equal("LaterCheckpointMirrorStagingInvalid",
                        Assert.Throws<GitSourceSnapshotMirrorException>(() =>
                            GitIncrementalLaterCheckpointMirrorStagingIntegrity
                                .Prepare(latestPlan, joined, fullRefs,
                                    bundlePreparation,
                                    committed with {
                                        TargetReceiptSha256 = new string('0', 64)
                                    }, first)).Code);

                    var storedF035 = await verifiedDb
                        .GitLaterCheckpointBundleEvidence.AsNoTracking()
                        .SingleAsync(x => x.WorkpackId == first.WorkpackId);
                    Assert.Equal(committed.EvidenceSha256,
                        storedF035.EvidenceSha256);
                    Assert.Equal(committed.BundleSha256,
                        storedF035.BundleSha256);
                    Assert.Equal(committed.BundleBytes,
                        storedF035.BundleBytes);
                    Assert.Equal(committed.SourceSnapshotId,
                        storedF035.SourceSnapshotId);
                    Assert.Equal(committed.TargetReceiptSha256,
                        storedF035.TargetReceiptSha256);
                    Assert.False(storedF035.ReadyForExecution ||
                        storedF035.CanPush || storedF035.CanRetry ||
                        storedF035.CanDispatch || storedF035.MigrationComplete);
                    var bundleEvents = await verifiedDb
                        .GitLaterCheckpointBundleEvents.AsNoTracking()
                        .Where(x => x.WorkpackId == first.WorkpackId)
                        .ToArrayAsync();
                    Assert.Single(bundleEvents);
                    Assert.Equal("Recorded", bundleEvents[0].Type);
                    Assert.Equal(committed.EvidenceSha256,
                        bundleEvents[0].EvidenceSha256);
                    var recordedLease = await verifiedDb
                        .GitLaterCheckpointBundleLeases.AsNoTracking()
                        .SingleAsync(x => x.WorkpackId == first.WorkpackId);
                    Assert.Equal("Recorded", recordedLease.State);
                    Assert.Equal(lease3.Generation, recordedLease.Generation);
                    Assert.Null(recordedLease.LeaseUntilUtc);
                    Assert.False(await coordinator.ReleaseAsync(
                        first.WorkpackId, lease3.LeaseToken,
                        lease3.Generation, candidate.RequestedBy,
                        lease3.BundlePlanSha256, default));
                    // Restart-like replay first rechecks immutable F-091,
                    // F-031, F-038, F-035 event and actual binary SHA/bytes.
                    // It does not reacquire a lease or run Git again.
                    var durable = auditScope.ServiceProvider.GetRequiredService<
                        PostgresGitIncrementalLaterCheckpointDurableBundleJournal>();
                    var replayReceipt = await durable.CaptureAsync(
                        run.Id, plan.Id, later.Id, observationId,
                        first.WorkpackId, "repo-1",
                        targetReceipt.EvidenceSha256, candidate.RequestedBy,
                        default);
                    Assert.Equal(committed, replayReceipt);
                    Assert.Single(await verifiedDb.GitLaterCheckpointBundleEvents
                        .AsNoTracking().Where(x => x.WorkpackId ==
                            first.WorkpackId).ToArrayAsync());
                    Assert.Equal("LaterCheckpointBundleLeaseAlreadyRecorded",
                        (await Assert.ThrowsAsync<GitSourceBundleException>(
                            () => coordinator.ClaimAsync(run.Id, plan.Id,
                                later.Id, observationId, first.WorkpackId,
                                "repo-1", targetReceipt.EvidenceSha256,
                                candidate.RequestedBy,
                                TimeSpan.FromMinutes(2), default))).Code);
                    Assert.Empty(await verifiedDb.GitSourceBundleEvidence
                        .AsNoTracking().Where(x => x.WorkpackId ==
                            first.WorkpackId).ToArrayAsync());

                    // SQL must prevent even a directly issued mutation
                    // of either the persisted physical proof or its event.
                    var tamperStored = await Assert.ThrowsAsync<
                        Npgsql.PostgresException>(() =>
                        verifiedDb.Database.ExecuteSqlInterpolatedAsync($"""
                            UPDATE git_later_checkpoint_bundle_evidence
                            SET can_push = {true}
                            WHERE workpack_id = {first.WorkpackId}
                            """));
                    Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException,
                        tamperStored.SqlState);
                    var removeBundleAudit = await Assert.ThrowsAsync<
                        Npgsql.PostgresException>(() =>
                        verifiedDb.Database.ExecuteSqlInterpolatedAsync($"""
                            DELETE FROM git_later_checkpoint_bundle_events
                            WHERE workpack_id = {first.WorkpackId}
                            """));
                    Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException,
                        removeBundleAudit.SqlState);
                }
            }

            // Revoke the historical F-027 review: future POSTs must fail,
            // but committed immutable audit history remains retrievable.
            await using (var db = fixture.NewContext())
            {
                var oldReview = await db.IncrementalReviews.SingleAsync(
                    x => x.PreparationId == run.Id);
                oldReview.State = IncrementalReviewStates.Revoked;
                await db.SaveChangesAsync();
            }
            using (var revoked = await SendAsync(HttpMethod.Post,
                root, Request(decision, candidate.EvidenceSha256)))
            {
                Assert.Equal(System.Net.HttpStatusCode.Conflict,
                    revoked.StatusCode);
                Assert.Contains("CheckpointHumanReviewCandidateUnverified",
                    await revoked.Content.ReadAsStringAsync());
            }
            using (var history = await SendAsync(HttpMethod.Get, root + query))
            {
                Assert.Equal(System.Net.HttpStatusCode.OK, history.StatusCode);
                using var json = JsonDocument.Parse(
                    await history.Content.ReadAsStringAsync());
                Assert.Equal(receiptId, json.RootElement.GetProperty("id").GetGuid());
            }
            using (var readAfterRevocation = await SendAsync(
                HttpMethod.Get, readUrl))
            {
                Assert.Equal(System.Net.HttpStatusCode.Conflict,
                    readAfterRevocation.StatusCode);
                Assert.Contains("CheckpointTargetReadPreflightSourceUnverified",
                    await readAfterRevocation.Content.ReadAsStringAsync());
            }
            using (var blockedObservation = await SendAsync(
                HttpMethod.Post, obsUrl, targetRequest))
            {
                Assert.Equal(System.Net.HttpStatusCode.Conflict,
                    blockedObservation.StatusCode);
                Assert.Contains("CheckpointTargetObservationPreflightUnverified",
                    await blockedObservation.Content.ReadAsStringAsync());
            }
            if (observationId != Guid.Empty)
            {
                // F-027 revocation makes new capture invalid, but the
                // previously sealed proof remains inspectable as HISTORY,
                // never a renewed approval.
                using (var revokedHistory = await SendAsync(
                    HttpMethod.Get,
                    "/api/incremental-preparations/" + run.Id +
                    "/operation-plans/" + plan.Id +
                    "/source-sync-checkpoints/" + later.Id +
                    "/later-workpacks/" + recordedWorkpackId +
                    "/history?sourceRepositoryId=repo-1" +
                    "&targetObservationId=" + observationId +
                    "&expectedTargetReceiptSha256=" + targetReceiptSha))
                {
                    Assert.Equal(System.Net.HttpStatusCode.OK,
                        revokedHistory.StatusCode);
                    using var data = JsonDocument.Parse(
                        await revokedHistory.Content.ReadAsStringAsync());
                    Assert.True(data.RootElement.GetProperty("historicalOnly")
                        .GetBoolean());
                    Assert.False(data.RootElement.GetProperty(
                        "currentApprovalVerified").GetBoolean());
                }
                await using var scoped = api.Host.Services.CreateAsyncScope();
                var historicalDurable = scoped.ServiceProvider
                    .GetRequiredService<
                        PostgresGitIncrementalLaterCheckpointDurableBundleJournal>();
                var historicalReceipt = await historicalDurable.CaptureAsync(
                    run.Id, plan.Id, later.Id, observationId,
                    recordedWorkpackId, "repo-1", targetReceiptSha!,
                    candidate.RequestedBy, default);
                Assert.Equal(recordedWorkpackId,
                    historicalReceipt.WorkpackId);
                Assert.False(historicalReceipt.CanPush ||
                    historicalReceipt.CanRetry ||
                    historicalReceipt.CanDispatch ||
                    historicalReceipt.MigrationComplete);
                // Tamper with actual bytes AFTER a committed proof: SQL
                // remains immutable, but the replay must fail closed.
                var physicalBundle = Path.Combine(
                    physicalMirror!.BundleRoot,
                    recordedWorkpackId.ToString("N") + ".bundle");
                await using (var changeFile = new FileStream(
                    physicalBundle, FileMode.Append, FileAccess.Write,
                    FileShare.None))
                    changeFile.WriteByte(0);
                Assert.Equal("BundleStoredSizeMismatch",
                    (await Assert.ThrowsAsync<GitSourceBundleException>(
                        () => historicalDurable.CaptureAsync(
                            run.Id, plan.Id, later.Id, observationId,
                            recordedWorkpackId, "repo-1", targetReceiptSha!,
                            candidate.RequestedBy, default))).Code);
                var bundleReader = scoped.ServiceProvider.GetRequiredService<
                    PostgresGitIncrementalLaterCheckpointBundleReadPreflight>();
                Assert.Equal("LaterCheckpointBundleReadProvenanceUnverified",
                    (await Assert.ThrowsAsync<GitSourceBundleException>(() =>
                        bundleReader.ReadAsync(run.Id, plan.Id, later.Id,
                            observationId, recordedWorkpackId, "repo-1",
                            targetReceiptSha!, default))).Code);
                var mirrorReader = scoped.ServiceProvider.GetRequiredService<
                    PostgresGitIncrementalLaterCheckpointMirrorStagingPreflight>();
                Assert.Equal("LaterCheckpointMirrorStagingProvenanceUnverified",
                    (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                        () => mirrorReader.ReadAsync(run.Id, plan.Id,
                            later.Id, observationId, recordedWorkpackId,
                            "repo-1", targetReceiptSha!, candidate.RequestedBy,
                            default))).Code);
                var writer = scoped.ServiceProvider.GetRequiredService<
                    PostgresGitIncrementalLaterCheckpointWorkpackJournal>();
                Assert.Equal("LaterCheckpointWorkpackPreflightUnverified",
                    (await Assert.ThrowsAsync<
                        GitIncrementalCheckpointWorkpackException>(() =>
                        writer.CaptureAsync(run.Id, plan.Id, later.Id,
                            observationId, "repo-1", targetReceiptSha!,
                            candidate.RequestedBy, default))).Code);
                var reader = scoped.ServiceProvider.GetRequiredService<
                    PostgresGitIncrementalLaterCheckpointAuditPreflight>();
                Assert.Equal("GitLaterCheckpointPreflightApprovalUnverified",
                    (await Assert.ThrowsAsync<
                        GitIncrementalCheckpointPreflightException>(() =>
                        reader.ReadAsync(run.Id, plan.Id, later.Id,
                            observationId, "repo-1", targetReceiptSha!,
                            default))).Code);
            }
            if (observationId != Guid.Empty)
            {
                using var history = await SendAsync(
                    HttpMethod.Get, obsUrl + "?sourceRepositoryId=repo-1");
                Assert.Equal(System.Net.HttpStatusCode.OK, history.StatusCode);
                using var json = JsonDocument.Parse(
                    await history.Content.ReadAsStringAsync());
                Assert.Equal(targetReceiptSha,
                    json.RootElement.GetProperty("evidenceSha256").GetString());
            }
            Assert.Equal(outboundBefore + (observationId != Guid.Empty ? 2 : 0),
                api.Calls.Count);
            await using (var db = fixture.NewContext())
            {
                Assert.Single(await db.GitCheckpointTargetHumanReviews
                    .AsNoTracking().Where(x => x.CheckpointId == later.Id)
                    .ToArrayAsync());
                Assert.Single(await db.GitCheckpointTargetHumanReviewEvents
                    .AsNoTracking().Where(x => x.DecisionId == receiptId)
                    .ToArrayAsync());
                var observedRows = await db.GitCheckpointTargetObservations
                    .AsNoTracking().Where(x => x.CheckpointId == later.Id)
                    .ToArrayAsync();
                var targetEvents = await db.GitCheckpointTargetObservationEvents
                    .AsNoTracking().Where(x => x.ObservationId == observationId)
                    .ToArrayAsync();
                if (observationId != Guid.Empty)
                {
                    Assert.Single(observedRows);
                    Assert.Single(targetEvents);
                    Assert.Equal(targetReceiptSha,
                        observedRows[0].EvidenceSha256);
                    Assert.Equal("ObservedReadOnly", targetEvents[0].Type);
                    // Database triggers independently refuse any attempt to
                    // flip a saved observation or rewrite its audit event.
                    var rewrite = await Assert.ThrowsAsync<
                        Npgsql.PostgresException>(() =>
                        db.Database.ExecuteSqlInterpolatedAsync($"""
                            UPDATE git_checkpoint_target_observations
                            SET can_push = {true}
                            WHERE id = {observationId}
                            """));
                    Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException,
                        rewrite.SqlState);
                    var eraseAudit = await Assert.ThrowsAsync<
                        Npgsql.PostgresException>(() =>
                        db.Database.ExecuteSqlInterpolatedAsync($"""
                            DELETE FROM git_checkpoint_target_observation_events
                            WHERE observation_id = {observationId}
                            """));
                    Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException,
                        eraseAudit.SqlState);
                    Assert.False(observedRows[0].CanPush ||
                        observedRows[0].CanRetry || observedRows[0].CanDispatch ||
                        observedRows[0].CanObserveTarget ||
                        observedRows[0].MigrationComplete);
                }
                else
                {
                    Assert.Empty(observedRows);
                    Assert.Empty(targetEvents);
                }
                Assert.Empty(await db.GitHubTargetRefEvidence.AsNoTracking()
                    .Where(x => x.PlanId == plan.Id).ToArrayAsync());
                Assert.Empty(await db.IncrementalGitRefReconciliations
                    .AsNoTracking().Where(x => x.SnapshotId ==
                        candidate.SourceSnapshotId).ToArrayAsync());
                Assert.False((await new PostgresIncrementalOperationPlanRepository(
                    db, clock).GetAsync(run.Id, plan.Id, default))!.IsCurrent);
            }

            object Request(string requestedDecision, string expected) => new {
                sourceRepositoryId = "repo-1",
                decision = requestedDecision,
                expectedCandidateEvidenceSha256 = expected,
                reviewedBy = candidate.RequestedBy, // malicious impersonation
                requestedBy = reviewerId               // malicious impersonation
            };
            async Task<System.Net.Http.HttpResponseMessage> SendAsync(
                System.Net.Http.HttpMethod method, string url,
                object? body = null, string? asToken = null)
            {
                using var request = new System.Net.Http.HttpRequestMessage(
                    method, url);
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue(
                        "Bearer", asToken ?? token);
                if (method == System.Net.Http.HttpMethod.Post)
                    request.Content = System.Net.Http.Json.JsonContent.Create(body);
                return await api.Client.SendAsync(request);
            }
        }
        finally
        {
            await api.DisposeAsync();
            physicalMirror?.Dispose();
        }
    }

    [Theory]
    [InlineData(40, GitCheckpointTargetHumanReviewStates.ApprovedForObservation)]
    [InlineData(64, GitCheckpointTargetHumanReviewStates.ApprovedForObservation)]
    [InlineData(40, GitCheckpointTargetHumanReviewStates.Rejected)]
    [InlineData(64, GitCheckpointTargetHumanReviewStates.Rejected)]
    public async Task F097c4c0b_real_second_epoch_independent_human_decision_is_append_only(
        int oidLength, string decision)
    {
        var clock = new PostgresIncrementalOperationPlanTests.ManualClock(
            DateTimeOffset.UtcNow);
        IncrementalPreparationView run;
        IncrementalOperationPlan plan;
        GitHubSourceSyncCheckpointReceipt later;
        GitCheckpointTargetReReviewCandidate candidate;
        GitCheckpointTargetHumanReviewDecision receipt;
        Guid requester;
        var reviewer = Guid.NewGuid();
        await using (var db = fixture.NewContext())
        {
            var setup = await SetupAsync(db, clock, oidLength);
            run = setup.Preparation;
            plan = setup.Plan;
            requester = setup.ActorId;
            var initial = await Ledger(db, clock).CaptureAsync(
                run.Id, plan.Id, setup.Checkpoint.Id,
                "repo-1", requester, default);
            var laterId = await LaterF014SnapshotAsync(
                db, clock, plan, oidLength);
            later = await new PostgresGitHubSourceSyncCheckpointJournal(
                db, clock, new(true),
                new PostgresIncrementalOperationPlanRepository(db, clock),
                new PostgresStagingRepository(db, clock)).CaptureAsync(
                    run.Id, plan.Id, laterId, setup.Checkpoint.Id,
                    plan.ManifestSha256, requester, default);
            var full = await Ledger(db, clock).CaptureAsync(
                run.Id, plan.Id, later.Id, "repo-1", requester, default);
            Assert.Equal(initial.EvidenceSha256,
                full.PreviousFullRefProofSha256);
            clock.Advance(TimeSpan.FromSeconds(2));
            candidate = await ReReview(db, clock).ReadAsync(
                run.Id, plan.Id, later.Id, "repo-1", requester, default);
            Assert.Equal(laterId, candidate.SourceSnapshotId);
            Assert.False(candidate.CanObserveTarget);
            var enabled = new PostgresGitCheckpointTargetHumanReviewJournal(
                db, clock, new(true), ReReview(db, clock));
            var disabled = new PostgresGitCheckpointTargetHumanReviewJournal(
                db, clock, new(false), ReReview(db, clock));
            Assert.Equal("CheckpointHumanReviewDisabled",
                (await Assert.ThrowsAsync<
                    GitCheckpointTargetHumanReviewException>(() =>
                    disabled.RecordAsync(run.Id, plan.Id, later.Id,
                        "repo-1", requester, reviewer, decision,
                        candidate.EvidenceSha256, default))).Code);
            Assert.Equal("CheckpointHumanReviewInvalidRequest",
                (await Assert.ThrowsAsync<
                    GitCheckpointTargetHumanReviewException>(() =>
                    enabled.RecordAsync(run.Id, plan.Id, later.Id,
                        "repo-1", requester, requester, decision,
                        candidate.EvidenceSha256, default))).Code);
            Assert.Equal("CheckpointHumanReviewCandidateChanged",
                (await Assert.ThrowsAsync<
                    GitCheckpointTargetHumanReviewException>(() =>
                    enabled.RecordAsync(run.Id, plan.Id, later.Id,
                        "repo-1", requester, reviewer, decision,
                        new string('0', 64), default))).Code);
            Assert.Empty(await db.GitCheckpointTargetHumanReviews
                .AsNoTracking().Where(x => x.PlanId == plan.Id)
                .ToArrayAsync());

            var presented = await enabled.ReadAuthenticatedCandidateAsync(
                run.Id, plan.Id, later.Id, "repo-1", default);
            Assert.Equal(candidate.EvidenceSha256, presented.EvidenceSha256);
            Assert.Equal(requester, presented.RequestedBy);
            Assert.Equal("CheckpointHumanReviewIndependentReviewerRequired",
                (await Assert.ThrowsAsync<
                    GitCheckpointTargetHumanReviewException>(() =>
                    enabled.RecordAuthenticatedAsync(run.Id, plan.Id,
                        later.Id, "repo-1", requester, decision,
                        candidate.EvidenceSha256, default))).Code);
            receipt = await enabled.RecordAuthenticatedAsync(
                run.Id, plan.Id, later.Id, "repo-1",
                reviewer, decision, candidate.EvidenceSha256, default);
            Assert.Equal(receipt, await enabled.InspectScopedAsync(
                run.Id, plan.Id, later.Id, "repo-1", default));
            Assert.Null(await enabled.InspectScopedAsync(
                run.Id, plan.Id, later.Id, "foreign-repo", default));
            Assert.Equal(1, receipt.EvidenceVersion);
            Assert.Equal(later.Id, receipt.CheckpointId);
            Assert.Equal(candidate.SourceSnapshotId, receipt.SourceSnapshotId);
            Assert.Equal(candidate.EvidenceSha256,
                receipt.CandidateEvidenceSha256);
            Assert.Equal(reviewer, receipt.ReviewedBy);
            Assert.Equal(decision, receipt.Decision);
            Assert.False(receipt.CanObserveTarget || receipt.CanPush ||
                receipt.CanRetry || receipt.CanDispatch ||
                receipt.MigrationComplete);
            GitCheckpointTargetHumanReviewIntegrity.Verify(
                receipt, candidate);

            // #109 distinct OFF-by-default read-preflight; neither
            // approval nor this witness can ever permit transport.
            var readOff = new PostgresGitCheckpointTargetReadPreflight(
                db, clock, new(false),
                new PostgresIncrementalOperationPlanRepository(db, clock),
                enabled);
            Assert.Equal("CheckpointTargetReadPreflightDisabled",
                (await Assert.ThrowsAsync<
                    GitCheckpointTargetReadPreflightException>(() =>
                    readOff.ReadAsync(run.Id, plan.Id, later.Id, "repo-1",
                        reviewer, candidate.EvidenceSha256,
                        receipt.EvidenceSha256, default))).Code);
            var readGate = new PostgresGitCheckpointTargetReadPreflight(
                db, clock, new(true),
                new PostgresIncrementalOperationPlanRepository(db, clock),
                enabled);
            if (decision == GitCheckpointTargetHumanReviewStates.ApprovedForObservation)
            {
                var readiness = await readGate.ReadAsync(
                    run.Id, plan.Id, later.Id, "repo-1",
                    reviewer, candidate.EvidenceSha256,
                    receipt.EvidenceSha256, default);
                GitCheckpointTargetReadPreflightIntegrity.Verify(
                    readiness, candidate, receipt);
                Assert.Equal(oidLength == 40 ? "sha1" : "sha256",
                    readiness.ObjectFormat);
                Assert.Equal(later.Id, readiness.CheckpointId);
                Assert.True(readiness.ExpiresAtUtc > readiness.ValidatedAtUtc);
                Assert.False(readiness.ProviderReadAuthorized ||
                    readiness.CanObserveTarget || readiness.CanPush ||
                    readiness.CanRetry || readiness.CanDispatch ||
                    readiness.MigrationComplete);
                Assert.Equal("CheckpointTargetReadPreflightIndependentReviewerRequired",
                    (await Assert.ThrowsAsync<
                        GitCheckpointTargetReadPreflightException>(() =>
                        readGate.ReadAsync(run.Id, plan.Id, later.Id, "repo-1",
                            requester, candidate.EvidenceSha256,
                            receipt.EvidenceSha256, default))).Code);
                Assert.Equal("CheckpointTargetReadPreflightCandidateChanged",
                    (await Assert.ThrowsAsync<
                        GitCheckpointTargetReadPreflightException>(() =>
                        readGate.ReadAsync(run.Id, plan.Id, later.Id, "repo-1",
                            reviewer, new string('0', 64),
                            receipt.EvidenceSha256, default))).Code);
                Assert.Equal("CheckpointTargetReadPreflightReviewChanged",
                    (await Assert.ThrowsAsync<
                        GitCheckpointTargetReadPreflightException>(() =>
                        readGate.ReadAsync(run.Id, plan.Id, later.Id, "repo-1",
                            reviewer, candidate.EvidenceSha256,
                            new string('0', 64), default))).Code);
                Assert.Equal("CheckpointTargetReadPreflightSourceUnverified",
                    (await Assert.ThrowsAsync<
                        GitCheckpointTargetReadPreflightException>(() =>
                        readGate.ReadAsync(run.Id, plan.Id, later.Id,
                            "other-repository", reviewer,
                            candidate.EvidenceSha256,
                            receipt.EvidenceSha256, default))).Code);
            }
            else
            {
                Assert.Equal("CheckpointTargetReadPreflightHumanApprovalRequired",
                    (await Assert.ThrowsAsync<
                        GitCheckpointTargetReadPreflightException>(() =>
                        readGate.ReadAsync(run.Id, plan.Id, later.Id, "repo-1",
                            reviewer, candidate.EvidenceSha256,
                            receipt.EvidenceSha256, default))).Code);
            }
            Assert.False((await new PostgresIncrementalOperationPlanRepository(
                db, clock).GetAsync(run.Id, plan.Id, default))!.IsCurrent);
            Assert.Empty(await db.GitHubTargetRefEvidence.AsNoTracking()
                .Where(x => x.PlanId == plan.Id).ToArrayAsync());
            Assert.Empty(await db.IncrementalGitRefReconciliations
                .AsNoTracking().Where(x => x.SnapshotId == laterId)
                .ToArrayAsync());
        }

        async Task<GitCheckpointTargetHumanReviewDecision> ReplayAsync()
        {
            await using var db = fixture.NewContext();
            return await new PostgresGitCheckpointTargetHumanReviewJournal(
                db, clock, new(true), ReReview(db, clock)).RecordAsync(
                    run.Id, plan.Id, later.Id, "repo-1",
                    requester, reviewer, decision,
                    candidate.EvidenceSha256, default);
        }
        var repeats = await Task.WhenAll(
            Enumerable.Range(0, 3).Select(_ => ReplayAsync()));
        Assert.All(repeats, x => Assert.Equal(receipt, x));

        await using (var db = fixture.NewContext())
        {
            Assert.Single(await db.GitCheckpointTargetHumanReviews
                .AsNoTracking().Where(x => x.CheckpointId == later.Id)
                .ToArrayAsync());
            Assert.Single(await db.GitCheckpointTargetHumanReviewEvents
                .AsNoTracking().Where(x => x.DecisionId == receipt.Id)
                .ToArrayAsync());
            var journal = new PostgresGitCheckpointTargetHumanReviewJournal(
                db, clock, new(true), ReReview(db, clock));
            // Readback remains verifiable after restart without granting
            // provider access or trusting an unscoped receipt GUID.
            Assert.Equal(receipt, await journal.InspectScopedAsync(
                run.Id, plan.Id, later.Id, "repo-1", default));
            Assert.Null(await journal.InspectScopedAsync(
                run.Id, Guid.NewGuid(), later.Id, "repo-1", default));
            Assert.Equal("CheckpointHumanReviewConflict",
                (await Assert.ThrowsAsync<
                    GitCheckpointTargetHumanReviewException>(() =>
                    journal.RecordAsync(run.Id, plan.Id, later.Id,
                        "repo-1", requester, Guid.NewGuid(), decision,
                        candidate.EvidenceSha256, default))).Code);
            Assert.Equal("CheckpointHumanReviewConflict",
                (await Assert.ThrowsAsync<
                    GitCheckpointTargetHumanReviewException>(() =>
                    journal.RecordAsync(run.Id, plan.Id, later.Id,
                        "repo-1", requester, reviewer,
                        decision == GitCheckpointTargetHumanReviewStates.Rejected
                            ? GitCheckpointTargetHumanReviewStates.ApprovedForObservation
                            : GitCheckpointTargetHumanReviewStates.Rejected,
                        candidate.EvidenceSha256, default))).Code);
            var mutation = await Assert.ThrowsAsync<
                Npgsql.PostgresException>(() =>
                    db.Database.ExecuteSqlInterpolatedAsync($"""
                        UPDATE git_checkpoint_target_human_reviews
                        SET can_observe_target = {true}
                        WHERE id = {receipt.Id}
                        """));
            Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException,
                mutation.SqlState);
            // The audit event is independently immutable as well:
            // an attacker must not be able to rewrite a rejection
            // as a purported human approval.
            var eventMutation = await Assert.ThrowsAsync<
                Npgsql.PostgresException>(() =>
                    db.Database.ExecuteSqlInterpolatedAsync($"""
                        UPDATE git_checkpoint_target_human_review_events
                        SET type = {"ApprovedForObservation"}
                        WHERE decision_id = {receipt.Id}
                        """));
            Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException,
                eventMutation.SqlState);

            // Revoking the old F-027 approval invalidates the source
            // prerequisite even when an earlier human decision exists.
            var oldReview = await db.IncrementalReviews.SingleAsync(
                x => x.PreparationId == run.Id);
            oldReview.State = IncrementalReviewStates.Revoked;
            await db.SaveChangesAsync();
            Assert.Equal("CheckpointHumanReviewCandidateUnverified",
                (await Assert.ThrowsAsync<
                    GitCheckpointTargetHumanReviewException>(() =>
                    journal.RecordAsync(run.Id, plan.Id, later.Id,
                        "repo-1", requester, reviewer, decision,
                        candidate.EvidenceSha256, default))).Code);
            oldReview.State = IncrementalReviewStates.ApprovedForPlanning;
            await db.SaveChangesAsync();

            clock.Advance(TimeSpan.FromMinutes(6));
            // Expired mutable validation prevents NEW approval, not
            // immutable historical decision audit/readback.
            Assert.Equal(receipt, await journal.InspectScopedAsync(
                run.Id, plan.Id, later.Id, "repo-1", default));
            Assert.Equal("CheckpointHumanReviewCandidateUnverified",
                (await Assert.ThrowsAsync<
                    GitCheckpointTargetHumanReviewException>(() =>
                    journal.RecordAsync(run.Id, plan.Id, later.Id,
                        "repo-1", requester, reviewer, decision,
                        candidate.EvidenceSha256, default))).Code);
        }
    }

    // Provider responses cannot manufacture a current F-031 proof.
    // This is a genuine two-epoch source, signed #108 human decision,
    // JWT-superadmin reviewer and fixed-host wire adapter. No provider
    // transport other than GitHub GET is ever issued.
    [Theory]
    [InlineData("hidden")]
    [InlineData("denied")]
    [InlineData("rate-limited")]
    [InlineData("redirect")]
    [InlineData("partial")]
    [InlineData("malformed")]
    [InlineData("wrong-oid")]
    [InlineData("expired-destination")]
    [InlineData("switched-organization")]
    public async Task F097c4c0d_target_provider_failures_do_not_persist_F031(
        string fault)
    {
        var clock = new PostgresIncrementalOperationPlanTests.ManualClock(
            DateTimeOffset.UtcNow);
        IncrementalPreparationView run;
        IncrementalOperationPlan plan;
        GitHubSourceSyncCheckpointReceipt later;
        GitCheckpointTargetReReviewCandidate candidate;
        GitCheckpointTargetHumanReviewDecision review;
        var reviewer = Guid.NewGuid();
        await using (var db = fixture.NewContext())
        {
            var seed = await SetupAsync(db, clock, 40);
            run = seed.Preparation;
            plan = seed.Plan;
            await Ledger(db, clock).CaptureAsync(
                run.Id, plan.Id, seed.Checkpoint.Id,
                "repo-1", seed.ActorId, default);
            var laterSnapshot = await LaterF014SnapshotAsync(
                db, clock, plan, 40);
            later = await new PostgresGitHubSourceSyncCheckpointJournal(
                db, clock, new(true),
                new PostgresIncrementalOperationPlanRepository(db, clock),
                new PostgresStagingRepository(db, clock)).CaptureAsync(
                    run.Id, plan.Id, laterSnapshot, seed.Checkpoint.Id,
                    plan.ManifestSha256, seed.ActorId, default);
            await Ledger(db, clock).CaptureAsync(
                run.Id, plan.Id, later.Id, "repo-1",
                seed.ActorId, default);
            candidate = await ReReview(db, clock).ReadAsync(
                run.Id, plan.Id, later.Id, "repo-1",
                seed.ActorId, default);
            var journal = new PostgresGitCheckpointTargetHumanReviewJournal(
                db, clock, new(true), ReReview(db, clock));
            review = await journal.RecordAuthenticatedAsync(
                run.Id, plan.Id, later.Id, "repo-1", reviewer,
                GitCheckpointTargetHumanReviewStates.ApprovedForObservation,
                candidate.EvidenceSha256, default);
        }
        if (fault is "expired-destination" or "switched-organization")
        {
            await using var db = fixture.NewContext();
            var destination = await db.Connections.SingleAsync(
                x => x.Id == plan.DestinationConnectionId);
            if (fault == "expired-destination")
                destination.ValidatedAtUtc = clock.GetUtcNow().AddMinutes(-6);
            else
                destination.Organization = "other-org";
            await db.SaveChangesAsync();
        }
        var api = new ApiHarness {
            EnableCheckpointTargetObservation = true,
            TargetObjectIdLength = 40,
            TargetObservationFault = fault
        };
        try
        {
            await api.InitializeAsync();
            var username = "fault-reviewer-" + Guid.NewGuid().ToString("N");
            await using (var scope = api.Host.Services.CreateAsyncScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<
                    Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
                var principal = new ApplicationUser {
                    Id = reviewer, UserName = username,
                    MustChangePassword = false
                };
                Assert.True((await users.CreateAsync(
                    principal, ApiHarness.SuperadminPassword)).Succeeded);
                Assert.True((await users.AddToRoleAsync(
                    principal, "superadmin")).Succeeded);
            }
            var jwt = await api.LoginAsync(
                username, ApiHarness.SuperadminPassword);
            using var request = new System.Net.Http.HttpRequestMessage(
                System.Net.Http.HttpMethod.Post,
                "/api/incremental-preparations/" + run.Id +
                "/operation-plans/" + plan.Id +
                "/source-sync-checkpoints/" + later.Id +
                "/target-ref-observations");
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer", jwt);
            request.Content = System.Net.Http.Json.JsonContent.Create(new {
                sourceRepositoryId = "repo-1",
                expectedCandidateEvidenceSha256 = candidate.EvidenceSha256,
                expectedHumanReviewEvidenceSha256 = review.EvidenceSha256
            });
            using var response = await api.Client.SendAsync(request);
            Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
            var nonProviderFault = fault is "expired-destination" or
                "switched-organization";
            Assert.Contains(nonProviderFault
                ? "CheckpointTargetObservationPreflightUnverified"
                : "CheckpointTargetObservationProviderUnverified",
                await response.Content.ReadAsStringAsync());
            var calls = api.Calls.ToArray();
            Assert.Equal(nonProviderFault ? 0 :
                fault == "wrong-oid" ? 2 : 1, calls.Length);
            Assert.All(calls, call =>
            {
                Assert.Equal("GET", call.Method);
                Assert.Equal("api.github.com", call.Url.Host);
                Assert.Equal("Bearer", call.AuthScheme);
            });
            await using var db = fixture.NewContext();
            Assert.Empty(await db.GitCheckpointTargetObservations.AsNoTracking()
                .Where(x => x.CheckpointId == later.Id).ToArrayAsync());
            Assert.Empty(await db.GitCheckpointTargetObservationEvents.AsNoTracking()
                .Where(x => x.ObservationId == review.Id).ToArrayAsync());
        }
        finally { await api.DisposeAsync(); }
    }

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

            // Even a fully verified BASELINE checkpoint must lose this
            // candidate role once F-091 has a newer source snapshot.
            Assert.Equal("CheckpointTargetReReviewSourceSuperseded",
                (await Assert.ThrowsAsync<GitCheckpointTargetReReviewException>(
                    () => ReReview(db, clock).ReadAsync(
                        run.Id, plan.Id, checkpoint.Id, "repo-1",
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
