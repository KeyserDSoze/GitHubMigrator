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
        new(db, TimeProvider.System, new(true));

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
        var first = await f.Materializer().MaterializeAsync(plan, default);
        await using (var db = fixture.NewContext())
        {
            var lease = await Journal(db).AcquireAsync(
                plan, Guid.NewGuid(), default);
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
