using System.Text.Json;
using System.Diagnostics;
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
        await GitCheckpointTargetHumanReviewSchema.InitializeAsync(services);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // Actual local Git mirror with object IDs bound to F-097b. Only
    // the test provisions this trusted root; no HTTP path or token.
    private sealed class CheckpointLocalMirror : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(),
            "f035v2-checkpoint-" + Guid.NewGuid().ToString("N"));
        public string SourceRoot => Path.Combine(root, "sources");
        public string BundleRoot => Path.Combine(root, "bundles");
        public IReadOnlyList<GitMirrorRef> Refs { get; }

        public CheckpointLocalMirror(Guid sourceId, int oidLength)
        {
            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(BundleRoot);
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
    [InlineData(40, GitCheckpointTargetHumanReviewStates.ApprovedForObservation)]
    [InlineData(64, GitCheckpointTargetHumanReviewStates.ApprovedForObservation)]
    [InlineData(64, GitCheckpointTargetHumanReviewStates.Rejected)]
    public async Task F097c4c0c_authenticated_http_review_of_real_later_checkpoint_is_immutable(
        int oidLength, string decision)
    {
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

        var api = new ApiHarness {
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
                    Assert.True(await coordinator.ReleaseAsync(
                        first.WorkpackId, lease1.LeaseToken,
                        lease1.Generation, candidate.RequestedBy,
                        lease1.BundlePlanSha256, default));
                    var lease2 = await coordinator.ClaimAsync(
                        run.Id, plan.Id, later.Id, observationId,
                        first.WorkpackId, "repo-1",
                        targetReceipt.EvidenceSha256,
                        candidate.RequestedBy, TimeSpan.FromMinutes(2),
                        default);
                    Assert.Equal(2L, lease2.Generation);
                    Assert.NotEqual(lease1.LeaseToken, lease2.LeaseToken);
                    Assert.False(await coordinator.ReleaseAsync(
                        first.WorkpackId, lease1.LeaseToken,
                        lease1.Generation, candidate.RequestedBy,
                        lease1.BundlePlanSha256, default));
                    var leaseEvents = await verifiedDb
                        .GitLaterCheckpointBundleLeaseEvents.AsNoTracking()
                        .Where(x => x.WorkpackId == first.WorkpackId)
                        .OrderBy(x => x.Id).ToArrayAsync();
                    Assert.Equal(new[] { "Acquired", "Released", "Acquired" },
                        leaseEvents.Select(x => x.Type));
                    Assert.Equal(new long[] { 1, 1, 2 },
                        leaseEvents.Select(x => x.Generation));
                    Assert.All(leaseEvents, ev =>
                        Assert.Equal(bundlePreparation.EvidenceSha256,
                            ev.BundlePlanSha256));
                    Assert.Empty(await verifiedDb.GitLaterCheckpointBundleEvidence
                        .AsNoTracking().Where(x => x.WorkpackId ==
                            first.WorkpackId).ToArrayAsync());

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
                            coordinator.CommitAsync(lease2, fakeFile,
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
                        lease2, rehearsed, run.Id, plan.Id, later.Id,
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
                    Assert.Equal(lease2.Generation, recordedLease.Generation);
                    Assert.Null(recordedLease.LeaseUntilUtc);
                    Assert.False(await coordinator.ReleaseAsync(
                        first.WorkpackId, lease2.LeaseToken,
                        lease2.Generation, candidate.RequestedBy,
                        lease2.BundlePlanSha256, default));
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
                var bundleReader = scoped.ServiceProvider.GetRequiredService<
                    PostgresGitIncrementalLaterCheckpointBundleReadPreflight>();
                Assert.Equal("LaterCheckpointBundleReadProvenanceUnverified",
                    (await Assert.ThrowsAsync<GitSourceBundleException>(() =>
                        bundleReader.ReadAsync(run.Id, plan.Id, later.Id,
                            observationId, recordedWorkpackId, "repo-1",
                            targetReceiptSha!, default))).Code);
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
