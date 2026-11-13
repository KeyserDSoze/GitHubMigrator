using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// PostgreSQL 17 + REAL F-060 SHA-1/SHA-256 Git bundles. Each action uses
// a separate DbContext, as independent API/worker replicas would do.
public sealed class PostgresGitSourceMirrorLifecycleTests(
    PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        using var services = new ServiceCollection()
            .AddDbContext<AppDbContext>(options =>
                options.UseNpgsql(fixture.ConnectionString))
            .BuildServiceProvider();
        await GitSourceMirrorLifecycleSchema.InitializeAsync(services);
    }
    public Task DisposeAsync() => Task.CompletedTask;

    private PostgresGitSourceMirrorLifecycleJournal Journal(AppDbContext db) =>
        new(db, new(true));

    private static Task<DateTimeOffset> DatabaseNowAsync(AppDbContext db) =>
        db.Database.SqlQueryRaw<DateTimeOffset>(
            "SELECT clock_timestamp() AS \"Value\"").SingleAsync();

    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    public async Task Later_F097_checkpoint_is_physically_verified_under_F061_database_lease(
        string gitFormat)
    {
        using var fixtureGit = new LocalGitSourceSnapshotMirrorMaterializerTests.Fixture();
        var legacy = fixtureGit.CreatePlan(gitFormat);
        var (plan, input, full, bundle) =
            LocalGitSourceCheckpointMirrorMaterializerTests.Fixture(legacy);
        var workpack = GitSourceCheckpointLocalMaterializationIntegrity.Bind(
            plan, input, full, bundle);
        Assert.NotEqual(plan.SnapshotId, workpack.SourceSnapshotId);
        Assert.NotEqual(
            GitSourceSnapshotMirrorIntegrity.PhysicalMirrorId(
                plan.SnapshotId, bundle.WorkpackId),
            workpack.PhysicalMirrorId);

        await using (var db = fixture.NewContext())
        {
            var disabled = new DurableGitSourceCheckpointMirrorMaterializer(
                fixtureGit.Materializer(), Journal(db));
            Assert.Equal("SourceCheckpointDurableMirrorDisabled",
                (await Assert.ThrowsAsync<
                    GitSourceCheckpointLocalMaterializationException>(() =>
                        disabled.MaterializeAsync(
                            plan, input, full, bundle, default))).Code);
            Assert.Null(await Journal(db).GetAsync(
                workpack.PhysicalMirrorId, default));

            // A forged F-097 input is refused BEFORE acquiring a lease.
            Assert.Equal("SourceCheckpointLocalWorkpackInvalid",
                (await Assert.ThrowsAsync<
                    GitSourceCheckpointLocalMaterializationException>(() =>
                        new DurableGitSourceCheckpointMirrorMaterializer(
                            fixtureGit.Materializer(), Journal(db), true)
                        .MaterializeAsync(
                            plan, input with {
                                SourceSnapshotId = plan.SnapshotId
                            }, full, bundle, default))).Code);
            Assert.Null(await Journal(db).GetAsync(
                workpack.PhysicalMirrorId, default));

            var receipt = await new DurableGitSourceCheckpointMirrorMaterializer(
                fixtureGit.Materializer(), Journal(db), true)
                .MaterializeAsync(plan, input, full, bundle, default);
            Assert.Equal(workpack.PhysicalMirrorId, receipt.PhysicalMirrorId);
            Assert.Equal(input.SourceSnapshotId, receipt.SourceSnapshotId);
            Assert.Equal(bundle.WorkpackId, receipt.WorkpackId);
            Assert.Equal(bundle.BundleSha256, receipt.BundleSha256);
            Assert.True(receipt.VerifiedObjectCount >= 9);
            Assert.Equal(workpack.LocalOnlyPlan.SourceRefs,
                fixtureGit.Refs(fixtureGit.Physical(workpack.LocalOnlyPlan)));
            Assert.False(receipt.RemotePushInvoked);
            Assert.False(receipt.RemoteWritesAuthorized);
            Assert.False(receipt.MayAutomaticallyRetry);
            Assert.False(receipt.MigrationComplete);
            var persisted = await Journal(db).GetAsync(
                workpack.PhysicalMirrorId, default);
            Assert.NotNull(persisted);
            Assert.Equal(GitSourceMirrorLifecycleStates.Verified, persisted.State);
            Assert.Equal(1L, persisted.Generation);
            Assert.Equal(receipt.EvidenceSha256, persisted.MirrorProofSha256);
            Assert.Equal(receipt.ObjectSetSha256, persisted.ObjectSetSha256);
            Assert.Null(persisted.LeasedBy);
            Assert.Null(persisted.ExpiresAtUtc);
            Assert.False(persisted.RemotePushInvoked);
            Assert.False(persisted.RemoteWritesAuthorized);
            Assert.False(persisted.MayAutomaticallyRetry);
            Assert.False(persisted.MigrationComplete);
            Assert.Equal(new[] { "LeaseAcquired", "Verified" },
                (await Journal(db).EventsAsync(
                    workpack.PhysicalMirrorId, default))
                    .Select(e => e.Type).ToArray());
        }

        // Fresh PostgreSQL connection and F-060 verification after crash/
        // restart. Reuse does NOT mean trusting the previous F-061 proof.
        await using (var db = fixture.NewContext())
        {
            var replay = await new DurableGitSourceCheckpointMirrorMaterializer(
                fixtureGit.Materializer(), Journal(db), true)
                .MaterializeAsync(plan, input, full, bundle, default);
            var status = await Journal(db).GetAsync(
                workpack.PhysicalMirrorId, default);
            Assert.NotNull(status);
            Assert.Equal(GitSourceMirrorLifecycleStates.Verified, status.State);
            Assert.Equal(2L, status.Generation);
            Assert.Equal(replay.EvidenceSha256, status.MirrorProofSha256);
            Assert.Equal(
                new[] { "LeaseAcquired", "Verified",
                    "ReverifyStarted", "Verified" },
                (await Journal(db).EventsAsync(
                    workpack.PhysicalMirrorId, default))
                    .Select(e => e.Type).ToArray());
        }
        Assert.Single(Directory.GetDirectories(
            fixtureGit.MirrorRoot, "*.git"));

        // Corrupt the immutable source-only bundle after a successful
        // F-061 receipt; the subsequent generation must be quarantined.
        var bundleFile = Path.Combine(
            fixtureGit.BundleRoot, bundle.BundleFileName);
        var bytes = await File.ReadAllBytesAsync(bundleFile);
        bytes[bytes.Length / 2] ^= 0x55;
        await File.WriteAllBytesAsync(bundleFile, bytes);
        await using (var db = fixture.NewContext())
        {
            var failure = await Assert.ThrowsAsync<
                GitSourceSnapshotMirrorException>(() =>
                    new DurableGitSourceCheckpointMirrorMaterializer(
                        fixtureGit.Materializer(), Journal(db), true)
                    .MaterializeAsync(plan, input, full, bundle, default));
            Assert.Equal("SourceMirrorBundleInvalid", failure.Code);
        }
        await using (var db = fixture.NewContext())
        {
            var state = await Journal(db).GetAsync(
                workpack.PhysicalMirrorId, default);
            Assert.NotNull(state);
            Assert.Equal(GitSourceMirrorLifecycleStates.RecoveryRequired,
                state.State);
            Assert.Equal("SourceMirrorBundleInvalid", state.FailureCode);
            Assert.Equal(3L, state.Generation);
            Assert.Equal("Quarantined",
                (await Journal(db).EventsAsync(
                    workpack.PhysicalMirrorId, default)).Last().Type);
            Assert.Equal("SourceMirrorLifecycleQuarantined",
                (await Assert.ThrowsAsync<GitSourceMirrorLifecycleException>(
                    () => Journal(db).AcquireAsync(
                        workpack.LocalOnlyPlan, Guid.NewGuid(),
                        default))).Code);
        }
    }

    [Fact]
    public async Task Later_checkpoint_three_workers_reverify_only_one_F061_generation_at_a_time()
    {
        using var fixtureGit = new LocalGitSourceSnapshotMirrorMaterializerTests.Fixture();
        var legacy = fixtureGit.CreatePlan("sha1");
        var (plan, input, full, bundle) =
            LocalGitSourceCheckpointMirrorMaterializerTests.Fixture(legacy);
        var workpack = GitSourceCheckpointLocalMaterializationIntegrity.Bind(
            plan, input, full, bundle);

        async Task<GitSourceSnapshotMirrorProof> RunAsync()
        {
            await using var db = fixture.NewContext();
            return await new DurableGitSourceCheckpointMirrorMaterializer(
                fixtureGit.Materializer(), Journal(db), true)
                .MaterializeAsync(plan, input, full, bundle, default);
        }
        var results = await Task.WhenAll(
            Enumerable.Range(0, 3).Select(_ => RunAsync()));
        Assert.All(results, proof =>
        {
            Assert.Equal(workpack.SourceSnapshotId, proof.SourceSnapshotId);
            Assert.Equal(workpack.PhysicalMirrorId, proof.PhysicalMirrorId);
            Assert.Equal(results[0].ObjectSetSha256,
                proof.ObjectSetSha256);
            Assert.False(proof.RemotePushInvoked);
            Assert.False(proof.MigrationComplete);
        });

        await using var inspect = fixture.NewContext();
        var status = await Journal(inspect).GetAsync(
            workpack.PhysicalMirrorId, default);
        Assert.NotNull(status);
        Assert.Equal(3L, status.Generation);
        Assert.Equal(GitSourceMirrorLifecycleStates.Verified, status.State);
        Assert.Equal(1, await inspect.GitSourceMirrorLifecycles
            .CountAsync(x => x.MirrorId == workpack.PhysicalMirrorId));
        Assert.Equal(new[] {
            "LeaseAcquired", "Verified", "ReverifyStarted", "Verified",
            "ReverifyStarted", "Verified"
        }, (await Journal(inspect).EventsAsync(
            workpack.PhysicalMirrorId, default))
            .Select(x => x.Type).ToArray());
        Assert.Single(Directory.GetDirectories(fixtureGit.MirrorRoot, "*.git"));
    }

    [Fact]
    public async Task Database_clock_bounds_admission_heartbeat_and_verified_receipt()
    {
        using var f = new LocalGitSourceSnapshotMirrorMaterializerTests.Fixture();
        var plan = f.CreatePlan();
        await using var db = fixture.NewContext();
        var beforeAcquire = await DatabaseNowAsync(db);
        var original = await Journal(db).AcquireAsync(
            plan, Guid.NewGuid(), default);
        var afterAcquire = await DatabaseNowAsync(db);
        var acquired = await Journal(db).GetAsync(plan.SourceMirrorId, default);
        Assert.NotNull(acquired);
        Assert.InRange(acquired.UpdatedAtUtc, beforeAcquire, afterAcquire);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(
            (acquired.UpdatedAtUtc +
                GitSourceMirrorLifecycleIntegrity.LeaseDuration)
            .ToUnixTimeMilliseconds()), original.ExpiresAtUtc);

        // Physical F-062 verification happens after the database's
        // admission clock and before a legitimate database renewal.
        var proof = await f.Materializer().MaterializeAsync(plan, default);
        await Task.Delay(20);
        var beforeRenew = await DatabaseNowAsync(db);
        var current = await Journal(db).RenewAsync(plan, original, default);
        var afterRenew = await DatabaseNowAsync(db);
        Assert.Equal(original.Generation, current.Generation);
        Assert.Equal(original.LeaseToken, current.LeaseToken);
        Assert.True(current.ExpiresAtUtc > original.ExpiresAtUtc);
        var renewed = Assert.Single((await Journal(db).EventsAsync(
            plan.SourceMirrorId, default))
            .Where(e => e.Type == "LeaseRenewed"));
        Assert.InRange(renewed.OccurredAtUtc, beforeRenew, afterRenew);
        // Renewal advances only the deadline, NOT the original F-062
        // admission boundary used for source proof freshness.
        var stillAdmitted = await Journal(db).GetAsync(
            plan.SourceMirrorId, default);
        Assert.Equal(acquired.UpdatedAtUtc, stillAdmitted!.UpdatedAtUtc);

        var beforeComplete = await DatabaseNowAsync(db);
        var verified = await Journal(db).CompleteAsync(
            plan, current, proof, default);
        var afterComplete = await DatabaseNowAsync(db);
        Assert.InRange(verified.UpdatedAtUtc, beforeComplete, afterComplete);
        var completionEvent = Assert.Single((await Journal(db).EventsAsync(
            plan.SourceMirrorId, default))
            .Where(e => e.Type == "Verified"));
        Assert.InRange(completionEvent.OccurredAtUtc,
            beforeComplete, afterComplete);
        Assert.False(verified.RemotePushInvoked);
        Assert.False(verified.MigrationComplete);
    }

    [Fact]
    public async Task An_expired_lease_is_reclaimed_using_live_PostgreSQL_time()
    {
        using var f = new LocalGitSourceSnapshotMirrorMaterializerTests.Fixture();
        var plan = f.CreatePlan();
        GitSourceMirrorLease old;
        await using (var db = fixture.NewContext())
            old = await Journal(db).AcquireAsync(
                plan, Guid.NewGuid(), default);

        // Simulate an expiring owner using PostgreSQL time only, not the
        // local worker clock. A concurrent admission waits for the same
        // advisory transaction lock until the original deadline passes.
        await using (var blocker = fixture.NewContext())
        {
            await using var lockTx = await blocker.Database.BeginTransactionAsync();
            await blocker.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(736061, hashtext({plan.SourceMirrorId.ToString()}))");
            await blocker.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE git_source_mirror_lifecycles
                SET lease_expires_at_utc=clock_timestamp() + interval '350 milliseconds'
                WHERE mirror_id={plan.SourceMirrorId}
                """);
            var contender = Task.Run(async () =>
            {
                await using var db = fixture.NewContext();
                return await Journal(db).AcquireAsync(
                    plan, Guid.NewGuid(), default);
            });
            // The contender opens its transaction while blocked; unlike
            // transaction_timestamp()/now(), clock_timestamp() must be
            // sampled only after the lock becomes available.
            await Task.Delay(600);
            await lockTx.CommitAsync();
            var successor = await contender.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(old.Generation + 1, successor.Generation);
            Assert.NotEqual(old.LeaseToken, successor.LeaseToken);
            await using var stale = fixture.NewContext();
            var denied = await Assert.ThrowsAsync<GitSourceMirrorLifecycleException>(
                () => Journal(stale).RenewAsync(plan, old, default));
            Assert.Equal("SourceMirrorLifecycleLeaseLost", denied.Code);
        }
    }

    [Fact]
    public async Task Six_replicas_admit_only_one_Git_import_generation()
    {
        using var f = new LocalGitSourceSnapshotMirrorMaterializerTests.Fixture();
        var plan = f.CreatePlan();
        async Task<(GitSourceMirrorLease? Lease, string? Failure)> Compete()
        {
            await using var db = fixture.NewContext();
            try
            {
                return (await Journal(db).AcquireAsync(
                    plan, Guid.NewGuid(), default), null);
            }
            catch (GitSourceMirrorLifecycleException ex)
            {
                return (null, ex.Code);
            }
        }
        var outcomes = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => Compete()));
        var winner = Assert.Single(outcomes.Where(x => x.Lease is not null)).Lease!;
        Assert.All(outcomes.Where(x => x.Lease is null), x =>
            Assert.Equal("SourceMirrorLifecycleBusy", x.Failure));

        // Actual Git import after winning the distributed database lease.
        var physical = await f.Materializer().MaterializeAsync(plan, default);
        await using (var db = fixture.NewContext())
        {
            var committed = await Journal(db).CompleteAsync(
                plan, winner, physical, default);
            Assert.Equal(GitSourceMirrorLifecycleStates.Verified, committed.State);
            Assert.Equal(1, committed.Generation);
            Assert.Equal(physical.EvidenceSha256, committed.MirrorProofSha256);
            Assert.Equal(physical.ObjectSetSha256, committed.ObjectSetSha256);
            Assert.False(committed.RemotePushInvoked);
            Assert.False(committed.RemoteWritesAuthorized);
            Assert.False(committed.MayAutomaticallyRetry);
            Assert.False(committed.MigrationComplete);
            Assert.Null(committed.LeasedBy);
            Assert.Null(committed.ExpiresAtUtc);
            var events = await Journal(db).EventsAsync(
                plan.SourceMirrorId, default);
            Assert.Equal(["LeaseAcquired", "Verified"],
                events.Select(x => x.Type).ToArray());
        }
        Assert.Single(Directory.GetDirectories(f.MirrorRoot, "*.git"));
    }

    [Fact]
    public async Task Crash_after_atomic_promotion_recovers_by_reverifying_not_trusting_orphan()
    {
        using var f = new LocalGitSourceSnapshotMirrorMaterializerTests.Fixture();
        var plan = f.CreatePlan("sha256");
        GitSourceMirrorLease abandoned;
        await using (var db = fixture.NewContext())
            abandoned = await Journal(db).AcquireAsync(
                plan, Guid.NewGuid(), default);
        var materializedBeforeCrash = await f.Materializer()
            .MaterializeAsync(plan, default);

        // Simulate owner crash after Git promoted the verified directory,
        // before it could commit the SQL checkpoint, then lease expiry.
        await using (var db = fixture.NewContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE git_source_mirror_lifecycles
                SET lease_expires_at_utc={DateTimeOffset.UtcNow.AddMinutes(-1)}
                WHERE mirror_id={plan.SourceMirrorId}
                """);
        GitSourceMirrorLease reclaimed;
        await using (var db = fixture.NewContext())
            reclaimed = await Journal(db).AcquireAsync(
                plan, Guid.NewGuid(), default);
        Assert.Equal(2, reclaimed.Generation);
        Assert.NotEqual(abandoned.LeaseToken, reclaimed.LeaseToken);
        Assert.NotEqual(abandoned.WorkerId, reclaimed.WorkerId);

        // An old lease can never acknowledge a newly published mirror.
        await using (var db = fixture.NewContext())
        {
            var ex = await Assert.ThrowsAsync<GitSourceMirrorLifecycleException>(
                () => Journal(db).CompleteAsync(
                    plan, abandoned, materializedBeforeCrash, default));
            Assert.Equal("SourceMirrorLifecycleLeaseLost", ex.Code);
        }

        var recoveredProof = await f.Materializer().MaterializeAsync(plan, default);
        Assert.Equal(materializedBeforeCrash.EvidenceSha256,
            recoveredProof.EvidenceSha256);
        await using (var db = fixture.NewContext())
        {
            var result = await Journal(db).CompleteAsync(
                plan, reclaimed, recoveredProof, default);
            Assert.Equal(GitSourceMirrorLifecycleStates.Verified, result.State);
            Assert.Equal(2, result.Generation);
            var events = await Journal(db).EventsAsync(
                plan.SourceMirrorId, default);
            Assert.Equal(["LeaseAcquired", "LeaseReclaimed", "Verified"],
                events.Select(e => e.Type).ToArray());
            Assert.Equal(1, await db.GitSourceMirrorLifecycles
                .CountAsync(x => x.WorkpackId == plan.SourceBundle!.WorkpackId));
        }
    }

    [Fact]
    public async Task The_same_scoped_DbContext_must_reload_after_Busy_and_before_fencing()
    {
        using var f = new LocalGitSourceSnapshotMirrorMaterializerTests.Fixture();
        var plan = f.CreatePlan();
        await using var first = fixture.NewContext();
        await using var second = fixture.NewContext();
        var original = await Journal(first).AcquireAsync(
            plan, Guid.NewGuid(), default);
        var busy = await Assert.ThrowsAsync<GitSourceMirrorLifecycleException>(
            () => Journal(second).AcquireAsync(
                plan, Guid.NewGuid(), default));
        Assert.Equal("SourceMirrorLifecycleBusy", busy.Code);

        var proof = await f.Materializer().MaterializeAsync(plan, default);
        await Journal(first).CompleteAsync(plan, original, proof, default);

        // This exact second DbContext previously tracked the row as
        // Leased: it MUST reload fresh Verified state before retrying.
        var replay = await Journal(second).AcquireAsync(
            plan, Guid.NewGuid(), default);
        Assert.Equal(2, replay.Generation);

        // The first DbContext also has a tracked older row, but can no
        // longer use its stale lease to quarantine the newer owner.
        var stale = await Assert.ThrowsAsync<GitSourceMirrorLifecycleException>(
            () => Journal(first).QuarantineAsync(
                plan, original, "SourceMirrorChanged", default));
        Assert.Equal("SourceMirrorLifecycleLeaseLost", stale.Code);

        var reverified = await f.Materializer().MaterializeAsync(plan, default);
        var committed = await Journal(second).CompleteAsync(
            plan, replay, reverified, default);
        Assert.Equal(GitSourceMirrorLifecycleStates.Verified, committed.State);
        Assert.Equal(proof.EvidenceSha256, committed.MirrorProofSha256);
    }

    [Fact]
    public async Task Reverification_of_a_corrupt_immutable_mirror_is_quarantined()
    {
        using var f = new LocalGitSourceSnapshotMirrorMaterializerTests.Fixture();
        var plan = f.CreatePlan();
        await using (var first = fixture.NewContext())
        {
            var lease = await Journal(first).AcquireAsync(
                plan, Guid.NewGuid(), default);
            var proof = await f.Materializer().MaterializeAsync(plan, default);
            await Journal(first).CompleteAsync(plan, lease, proof, default);
        }
        var broken = f.Physical(plan);
        f.Git("--git-dir=" + broken, "repack", "-a", "-d");
        f.Git("--git-dir=" + broken, "prune-packed");
        var pack = Assert.Single(Directory.GetFiles(
            Path.Combine(broken, "objects", "pack"), "*.pack"));
        File.SetUnixFileMode(pack,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await File.WriteAllBytesAsync(pack, [1, 2, 3]);

        await using (var recovering = fixture.NewContext())
        {
            var wrapper = new DurableGitSourceSnapshotMirrorMaterializer(
                f.Materializer(), Journal(recovering));
            var failed = await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                () => wrapper.MaterializeAsync(plan, default));
            Assert.Equal("SourceMirrorGitClosureInvalid", failed.Code);
        }
        await using (var db = fixture.NewContext())
        {
            var stored = await Journal(db).GetAsync(plan.SourceMirrorId, default);
            Assert.NotNull(stored);
            Assert.Equal(GitSourceMirrorLifecycleStates.RecoveryRequired,
                stored.State);
            Assert.Equal("SourceMirrorGitClosureInvalid", stored.FailureCode);
            Assert.NotNull(stored.MirrorProofSha256);
            var refusal = await Assert.ThrowsAsync<GitSourceMirrorLifecycleException>(
                () => Journal(db).AcquireAsync(
                    plan, Guid.NewGuid(), default));
            Assert.Equal("SourceMirrorLifecycleQuarantined", refusal.Code);
            Assert.Contains(await Journal(db).EventsAsync(
                plan.SourceMirrorId, default),
                e => e.Type == "Quarantined");
        }
        Assert.True(Directory.Exists(broken));
    }

    [Fact]
    public async Task Repeated_complete_Git_import_with_three_workers_preserves_one_row()
    {
        using var f = new LocalGitSourceSnapshotMirrorMaterializerTests.Fixture();
        var plan = f.CreatePlan();
        async Task<GitSourceSnapshotMirrorProof> Run()
        {
            await using var db = fixture.NewContext();
            return await new DurableGitSourceSnapshotMirrorMaterializer(
                f.Materializer(), Journal(db))
                .MaterializeAsync(plan, default);
        }
        var results = await Task.WhenAll(
            Enumerable.Range(0, 3).Select(_ => Run()));
        Assert.All(results, x =>
            Assert.Equal(results[0].EvidenceSha256, x.EvidenceSha256));
        await using var inspect = fixture.NewContext();
        var row = await Journal(inspect).GetAsync(plan.SourceMirrorId, default);
        Assert.NotNull(row);
        Assert.Equal(3, row.Generation);
        Assert.Equal(GitSourceMirrorLifecycleStates.Verified, row.State);
        Assert.Equal(1, await inspect.GitSourceMirrorLifecycles
            .CountAsync(x => x.MirrorId == plan.SourceMirrorId));
        Assert.Equal(6, (await Journal(inspect).EventsAsync(
            plan.SourceMirrorId, default)).Count);
    }

    [Fact]
    public async Task Proof_and_plan_conflicts_never_replace_the_first_verified_SHA()
    {
        using var f = new LocalGitSourceSnapshotMirrorMaterializerTests.Fixture();
        var plan = f.CreatePlan();
        GitSourceSnapshotMirrorProof first;
        await using (var db = fixture.NewContext())
        {
            var lease = await Journal(db).AcquireAsync(
                plan, Guid.NewGuid(), default);
            first = await f.Materializer().MaterializeAsync(plan, default);
            await Journal(db).CompleteAsync(plan, lease, first, default);
        }
        await using (var db = fixture.NewContext())
        {
            var changed = plan with { SourceBundle = plan.SourceBundle! with
            {
                BundleSha256 = new string('f', 64)
            }};
            var ex = await Assert.ThrowsAsync<GitSourceMirrorLifecycleException>(
                () => Journal(db).AcquireAsync(
                    changed, Guid.NewGuid(), default));
            Assert.Equal("SourceMirrorLifecyclePlanConflict", ex.Code);
        }
        await using (var db = fixture.NewContext())
        {
            var lease = await Journal(db).AcquireAsync(
                plan, Guid.NewGuid(), default);
            var fake = GitSourceSnapshotMirrorIntegrity.Seal(
                plan, new string('e', 64), first.VerifiedObjectCount,
                DateTimeOffset.UtcNow);
            var ex = await Assert.ThrowsAsync<GitSourceMirrorLifecycleException>(
                () => Journal(db).CompleteAsync(plan, lease, fake, default));
            Assert.Equal("SourceMirrorLifecycleProofConflict", ex.Code);
            await Journal(db).QuarantineAsync(
                plan, lease, ex.Code, default);
        }
        await using (var db = fixture.NewContext())
        {
            var status = await Journal(db).GetAsync(plan.SourceMirrorId, default);
            Assert.Equal(first.EvidenceSha256, status!.MirrorProofSha256);
            Assert.Equal(GitSourceMirrorLifecycleStates.RecoveryRequired,
                status.State);
            Assert.False(status.MayAutomaticallyRetry);
        }
    }

    [Fact]
    public async Task A_completed_generation_proof_cannot_be_replayed_under_a_new_lease()
    {
        using var f = new LocalGitSourceSnapshotMirrorMaterializerTests.Fixture();
        var plan = f.CreatePlan();
        GitSourceSnapshotMirrorProof oldProof;
        await using (var firstDb = fixture.NewContext())
        {
            var first = await Journal(firstDb).AcquireAsync(
                plan, Guid.NewGuid(), default);
            oldProof = await f.Materializer().MaterializeAsync(plan, default);
            await Journal(firstDb).CompleteAsync(
                plan, first, oldProof, default);
        }

        // Ensure the second acquisition is strictly later, even on
        // hosts with coarse wall-clock precision.
        await Task.Delay(20);
        await using (var secondDb = fixture.NewContext())
        {
            var second = await Journal(secondDb).AcquireAsync(
                plan, Guid.NewGuid(), default);
            Assert.Equal(2, second.Generation);
            var refusal = await Assert.ThrowsAsync<GitSourceMirrorLifecycleException>(
                () => Journal(secondDb).CompleteAsync(
                    plan, second, oldProof, default));
            Assert.Equal("SourceMirrorLifecycleProofStale", refusal.Code);
            await Journal(secondDb).QuarantineAsync(
                plan, second, refusal.Code, default);
        }

        await using var inspect = fixture.NewContext();
        var row = await Journal(inspect).GetAsync(
            plan.SourceMirrorId, default);
        Assert.NotNull(row);
        Assert.Equal(GitSourceMirrorLifecycleStates.RecoveryRequired,
            row.State);
        Assert.Equal(oldProof.EvidenceSha256, row.MirrorProofSha256);
        Assert.Equal("SourceMirrorLifecycleProofStale", row.FailureCode);
        Assert.False(row.MayAutomaticallyRetry);
        Assert.Equal(
            ["LeaseAcquired", "Verified", "ReverifyStarted", "Quarantined"],
            (await Journal(inspect).EventsAsync(plan.SourceMirrorId, default))
                .Select(e => e.Type).ToArray());
    }

    [Fact]
    public async Task Renewed_lease_fences_old_deadline_but_preserves_original_proof_floor()
    {
        using var f = new LocalGitSourceSnapshotMirrorMaterializerTests.Fixture();
        var plan = f.CreatePlan();
        GitSourceMirrorLease admitted;
        await using (var db = fixture.NewContext())
            admitted = await Journal(db).AcquireAsync(
                plan, Guid.NewGuid(), default);

        // The real Git fsck receipt predates the renewal, but was produced
        // after lease admission: renewal must not invalidate F-062.
        var physicalProof = await f.Materializer().MaterializeAsync(plan, default);
        await Task.Delay(20);
        GitSourceMirrorLease renewed;
        await using (var db = fixture.NewContext())
            renewed = await Journal(db).RenewAsync(plan, admitted, default);

        Assert.Equal(admitted.LeaseToken, renewed.LeaseToken);
        Assert.Equal(admitted.Generation, renewed.Generation);
        Assert.Equal(admitted.WorkerId, renewed.WorkerId);
        Assert.True(renewed.ExpiresAtUtc > admitted.ExpiresAtUtc);

        await using (var staleDb = fixture.NewContext())
        {
            var refused = await Assert.ThrowsAsync<GitSourceMirrorLifecycleException>(
                () => Journal(staleDb).CompleteAsync(
                    plan, admitted, physicalProof, default));
            Assert.Equal("SourceMirrorLifecycleLeaseLost", refused.Code);
        }
        await using (var currentDb = fixture.NewContext())
        {
            var status = await Journal(currentDb).CompleteAsync(
                plan, renewed, physicalProof, default);
            Assert.Equal(GitSourceMirrorLifecycleStates.Verified, status.State);
            Assert.Equal(1, status.Generation);
            Assert.Equal(physicalProof.EvidenceSha256, status.MirrorProofSha256);
            Assert.False(status.RemotePushInvoked);
            Assert.False(status.MigrationComplete);
            Assert.Equal(
                ["LeaseAcquired", "LeaseRenewed", "Verified"],
                (await Journal(currentDb).EventsAsync(
                    plan.SourceMirrorId, default))
                    .Select(x => x.Type).ToArray());
        }
    }

    [Fact]
    public async Task Expired_or_reclaimed_generation_cannot_be_renewed()
    {
        using var f = new LocalGitSourceSnapshotMirrorMaterializerTests.Fixture();
        var plan = f.CreatePlan();
        GitSourceMirrorLease old;
        await using (var db = fixture.NewContext())
            old = await Journal(db).AcquireAsync(
                plan, Guid.NewGuid(), default);
        await using (var db = fixture.NewContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE git_source_mirror_lifecycles
                SET lease_expires_at_utc={DateTimeOffset.UtcNow.AddMinutes(-1)}
                WHERE mirror_id={plan.SourceMirrorId}
                """);
        await using (var db = fixture.NewContext())
        {
            var ex = await Assert.ThrowsAsync<GitSourceMirrorLifecycleException>(
                () => Journal(db).RenewAsync(plan, old, default));
            Assert.Equal("SourceMirrorLifecycleLeaseLost", ex.Code);
        }
        GitSourceMirrorLease newOwner;
        await using (var db = fixture.NewContext())
            newOwner = await Journal(db).AcquireAsync(
                plan, Guid.NewGuid(), default);
        Assert.Equal(2, newOwner.Generation);
        await using (var db = fixture.NewContext())
        {
            var ex = await Assert.ThrowsAsync<GitSourceMirrorLifecycleException>(
                () => Journal(db).RenewAsync(plan, old, default));
            Assert.Equal("SourceMirrorLifecycleLeaseLost", ex.Code);
        }
        await using (var db = fixture.NewContext())
        {
            var renewed = await Journal(db).RenewAsync(plan, newOwner, default);
            Assert.Equal(newOwner.LeaseToken, renewed.LeaseToken);
            Assert.Equal(2, renewed.Generation);
            var events = await Journal(db).EventsAsync(
                plan.SourceMirrorId, default);
            Assert.Equal(["LeaseAcquired", "LeaseReclaimed", "LeaseRenewed"],
                events.Select(x => x.Type).ToArray());
        }
    }

    [Fact]
    public async Task Two_hour_absolute_lease_limit_stops_liveness_renewals()
    {
        using var f = new LocalGitSourceSnapshotMirrorMaterializerTests.Fixture();
        var plan = f.CreatePlan();
        GitSourceMirrorLease lease;
        await using (var db = fixture.NewContext())
            lease = await Journal(db).AcquireAsync(
                plan, Guid.NewGuid(), default);
        // Simulate an exceedingly long-lived worker while its current
        // deadline remains valid: no infinite lease extension is allowed.
        await using (var db = fixture.NewContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE git_source_mirror_lifecycles
                SET updated_at_utc={DateTimeOffset.UtcNow.AddHours(-3)}
                WHERE mirror_id={plan.SourceMirrorId}
                """);
        await using (var db = fixture.NewContext())
        {
            var ex = await Assert.ThrowsAsync<GitSourceMirrorLifecycleException>(
                () => Journal(db).RenewAsync(plan, lease, default));
            Assert.Equal("SourceMirrorLifecycleTimeLimit", ex.Code);
            Assert.Equal(["LeaseAcquired"],
                (await Journal(db).EventsAsync(plan.SourceMirrorId, default))
                    .Select(x => x.Type).ToArray());
        }
    }

    [Fact]
    public async Task Direct_SQL_cannot_turn_source_audit_into_remote_execution_or_edit_events()
    {
        using var f = new LocalGitSourceSnapshotMirrorMaterializerTests.Fixture();
        var plan = f.CreatePlan();
        await using var db = fixture.NewContext();
        var lease = await Journal(db).AcquireAsync(
            plan, Guid.NewGuid(), default);
        var proof = await f.Materializer().MaterializeAsync(plan, default);
        await Journal(db).CompleteAsync(plan, lease, proof, default);
        var id = plan.SourceMirrorId;

        Func<Task>[] forbidden = [
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE git_source_mirror_lifecycles SET remote_push_invoked={true} WHERE mirror_id={id}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE git_source_mirror_lifecycles SET remote_writes_authorized={true} WHERE mirror_id={id}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE git_source_mirror_lifecycles SET may_automatically_retry={true} WHERE mirror_id={id}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE git_source_mirror_lifecycles SET migration_complete={true} WHERE mirror_id={id}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE git_source_mirror_lifecycles SET bundle_sha256={new string('a', 64)} WHERE mirror_id={id}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE git_source_mirror_lifecycles SET mirror_proof_sha256={new string('b', 64)} WHERE mirror_id={id}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM git_source_mirror_lifecycles WHERE mirror_id={id}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM git_source_mirror_lifecycle_events WHERE mirror_id={id}")
        ];
        foreach (var write in forbidden)
            await Assert.ThrowsAnyAsync<Exception>(write);

        var status = await Journal(db).GetAsync(id, default);
        Assert.Equal(proof.EvidenceSha256, status!.MirrorProofSha256);
        Assert.Equal(2,
            (await Journal(db).EventsAsync(id, default)).Count);
    }
}
