using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

public sealed class PostgresGitHubTransferAttemptTests(
    PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>, IAsyncLifetime
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(20);

    public async Task InitializeAsync()
    {
        using var services = new ServiceCollection()
            .AddDbContext<AppDbContext>(options =>
                options.UseNpgsql(fixture.ConnectionString)).BuildServiceProvider();
        await GitHubTargetPreviewLeaseSchema.InitializeAsync(services);
        await GitHubTransferAttemptSchema.InitializeAsync(services);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Concurrent_replicas_prepare_only_one_target_and_keep_one_event()
    {
        var packet = Packet();
        var owner = Guid.NewGuid();
        await using var leaseDb = fixture.NewContext();
        var leaseStore = new PostgresGitHubTargetPreviewLeaseStore(
            leaseDb, TimeProvider.System);
        var lease = await leaseStore.AcquireAsync(
            packet, owner, Duration, default);
        var draft = Draft(packet, lease);
        async Task<GitHubTransferAttemptView> SubmitAsync()
        {
            await using var db = fixture.NewContext();
            return await Store(db).PrepareAsync(draft, default);
        }

        var results = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => SubmitAsync()));
        Assert.Single(results.Select(x => x.Id).Distinct());
        var prepared = results[0];
        Assert.Equal(GitHubTransferAttemptStates.AwaitingIndependentReview,
            prepared.State);
        Assert.False(prepared.TransportAttempted);
        Assert.False(prepared.ExecutionAuthorized);
        Assert.False(prepared.RemoteWritesPerformed);
        Assert.False(prepared.ReadyForExecution);

        await using var verify = fixture.NewContext();
        var store = Store(verify);
        Assert.Single((await store.EventsAsync(
            packet.PreparationId, packet.PlanId, packet.ScopeId,
            prepared.Id, default)).Where(x => x.Type == "Prepared"));
        Assert.Throws<GitHubTransferAttemptException>(() =>
            GitHubTransferAttemptIntegrity.Build(
                packet, Policy(packet) with { ExecutionAuthorized = true },
                lease.LeaseToken, lease.Generation, owner,
                DateTimeOffset.UtcNow));
        var conflict = await Assert.ThrowsAsync<GitHubTransferAttemptException>(
            () => store.PrepareAsync(draft with {
                PolicySha256 = Sha('f') }, default));
        Assert.Equal("TransferPreparationConflictingReplay", conflict.Code);

        var otherScope = packet with { ScopeId = Guid.NewGuid() };
        var competitor = draft with { ScopeId = otherScope.ScopeId };
        var busy = await Assert.ThrowsAsync<GitHubTransferAttemptException>(
            () => store.PrepareAsync(competitor, default));
        Assert.Equal("TransferPreparationBusy", busy.Code);

        Assert.True(await leaseStore.ReleaseAsync(packet.RepositoryId,
            owner, lease.LeaseToken, lease.Generation, packet.EvidenceSha256,
            default));
    }

    [Fact]
    public async Task Independent_reviewer_is_required_and_a_worker_stops_at_write_barrier()
    {
        var packet = Packet();
        var owner = Guid.NewGuid();
        var reviewer = Guid.NewGuid();
        await using var db = fixture.NewContext();
        var leaseStore = new PostgresGitHubTargetPreviewLeaseStore(
            db, TimeProvider.System);
        var lease = await leaseStore.AcquireAsync(packet, owner, Duration, default);
        var store = Store(db);
        var prepared = await store.PrepareAsync(Draft(packet, lease), default);
        var args = new object?[] {
            packet.PreparationId, packet.PlanId, packet.ScopeId, prepared.Id
        };

        var sameActor = await Assert.ThrowsAsync<GitHubTransferAttemptException>(
            () => store.DecideAsync(packet.PreparationId, packet.PlanId,
                packet.ScopeId, prepared.Id, owner, "acknowledge-dry-run", default));
        Assert.Equal("TransferPreparationTwoPersonRequired", sameActor.Code);

        var reviewed = await store.DecideAsync(
            packet.PreparationId, packet.PlanId, packet.ScopeId,
            prepared.Id, reviewer, "acknowledge-dry-run", default);
        Assert.Equal(GitHubTransferAttemptStates.Reviewed, reviewed.State);
        Assert.Equal(reviewer, reviewed.ReviewedBy);
        var doubleReview = await Assert.ThrowsAsync<GitHubTransferAttemptException>(
            () => store.DecideAsync(packet.PreparationId, packet.PlanId,
                packet.ScopeId, prepared.Id, Guid.NewGuid(),
                "acknowledge-dry-run", default));
        Assert.Equal("TransferPreparationAlreadyDecided", doubleReview.Code);

        // Two independent DbContexts simulate concurrent worker replicas.
        await using var otherDb = fixture.NewContext();
        var outcomes = await Task.WhenAll(
            store.BlockNextAtWriteBarrierAsync(default),
            Store(otherDb).BlockNextAtWriteBarrierAsync(default));
        Assert.Single(outcomes.Where(x => x?.Id == prepared.Id));
        var complete = await store.GetAsync(
            packet.PreparationId, packet.PlanId, packet.ScopeId, prepared.Id, default);
        Assert.Equal(GitHubTransferAttemptStates.BlockedAtWriteBarrier,
            complete!.State);
        Assert.False(complete.TransportAttempted);
        Assert.False(complete.ExecutionAuthorized);
        Assert.False(complete.RemoteWritesPerformed);
        Assert.False(complete.ReadyForExecution);
        Assert.DoesNotContain("LeaseToken",
            System.Text.Json.JsonSerializer.Serialize(complete));
        var events = await store.EventsAsync(
            packet.PreparationId, packet.PlanId, packet.ScopeId, prepared.Id, default);
        Assert.Equal(new[] { "Prepared", "Reviewed", "WriteBarrierBlocked" },
            events.Select(x => x.Type));
        Assert.Equal(1, events.Count(x => x.Type == "WriteBarrierBlocked"));
        Assert.True(await leaseStore.ReleaseAsync(packet.RepositoryId,
            owner, lease.LeaseToken, lease.Generation, packet.EvidenceSha256, default));
    }

    [Fact]
    public async Task Expired_or_replaced_lease_cannot_prepare_a_new_attempt()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var packet = Packet();
        var actor = Guid.NewGuid();
        await using var db = fixture.NewContext();
        var leaseStore = new PostgresGitHubTargetPreviewLeaseStore(db, clock);
        var lease = await leaseStore.AcquireAsync(packet, actor, Duration, default);
        clock.Advance(TimeSpan.FromSeconds(21));
        var draft = Draft(packet, lease) with {
            PolicyObservedAtUtc = clock.GetUtcNow()
        };
        var ex = await Assert.ThrowsAsync<GitHubTransferAttemptException>(
            () => Store(db, clock).PrepareAsync(draft, default));
        Assert.Equal("TransferPreparationLeaseLost", ex.Code);
        Assert.Empty(await db.GitHubTransferAttempts.AsNoTracking()
            .Where(x => x.RepositoryId == packet.RepositoryId).ToArrayAsync());
    }

    [Fact]
    public async Task Sql_constraints_prevent_all_remote_transport_and_execution_flags()
    {
        var packet = Packet();
        var owner = Guid.NewGuid();
        await using var db = fixture.NewContext();
        var leaseStore = new PostgresGitHubTargetPreviewLeaseStore(
            db, TimeProvider.System);
        var lease = await leaseStore.AcquireAsync(packet, owner, Duration, default);
        var store = Store(db);
        var prepared = await store.PrepareAsync(Draft(packet, lease), default);

        Func<Task>[] illegalWrites = [
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE github_transfer_attempts SET transport_attempted = {true} WHERE id = {prepared.Id}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE github_transfer_attempts SET execution_authorized = {true} WHERE id = {prepared.Id}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE github_transfer_attempts SET remote_writes_performed = {true} WHERE id = {prepared.Id}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE github_transfer_attempts SET ready_for_execution = {true} WHERE id = {prepared.Id}")
        ];
        foreach (var blocked in illegalWrites)
            await Assert.ThrowsAnyAsync<Exception>(blocked);
        var stored = await store.GetAsync(
            packet.PreparationId, packet.PlanId, packet.ScopeId, prepared.Id,
            default);
        Assert.False(stored!.TransportAttempted);
        Assert.False(stored.ExecutionAuthorized);
        Assert.False(stored.RemoteWritesPerformed);
        Assert.False(stored.ReadyForExecution);
        Assert.True(await leaseStore.ReleaseAsync(packet.RepositoryId,
            owner, lease.LeaseToken, lease.Generation, packet.EvidenceSha256, default));
    }

    [Fact]
    public async Task Reviewer_rejection_is_terminal_and_never_enqueued_for_worker()
    {
        var packet = Packet();
        var owner = Guid.NewGuid();
        await using var db = fixture.NewContext();
        var leaseStore = new PostgresGitHubTargetPreviewLeaseStore(
            db, TimeProvider.System);
        var lease = await leaseStore.AcquireAsync(packet, owner, Duration, default);
        var store = Store(db);
        var prepared = await store.PrepareAsync(Draft(packet, lease), default);
        var rejected = await store.DecideAsync(packet.PreparationId,
            packet.PlanId, packet.ScopeId, prepared.Id, Guid.NewGuid(),
            "reject", default);
        Assert.Equal(GitHubTransferAttemptStates.Rejected, rejected.State);
        var blocked = await store.BlockNextAtWriteBarrierAsync(default);
        Assert.True(blocked is null || blocked.Id != prepared.Id);
        Assert.False((await store.GetAsync(packet.PreparationId,
            packet.PlanId, packet.ScopeId, prepared.Id, default))!.TransportAttempted);
        Assert.True(await leaseStore.ReleaseAsync(packet.RepositoryId,
            owner, lease.LeaseToken, lease.Generation, packet.EvidenceSha256, default));
    }

    [Fact]
    public async Task Abandoned_pending_attempt_is_expired_and_audited_once()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var packet = Packet();
        var owner = Guid.NewGuid();
        await using var db = fixture.NewContext();
        var leaseStore = new PostgresGitHubTargetPreviewLeaseStore(db, clock);
        var lease = await leaseStore.AcquireAsync(packet, owner, Duration, default);
        var store = Store(db, clock);
        var prepared = await store.PrepareAsync(Draft(packet, lease), default);
        clock.Advance(TimeSpan.FromSeconds(21));
        Assert.True(await store.ExpireNextStaleAsync(default));
        Assert.False(await store.ExpireNextStaleAsync(default));
        var expired = await store.GetAsync(
            packet.PreparationId, packet.PlanId, packet.ScopeId, prepared.Id,
            default);
        Assert.Equal(GitHubTransferAttemptStates.Expired, expired!.State);
        Assert.NotNull(expired.ExpiredAtUtc);
        Assert.Null(expired.ReviewedBy);
        Assert.False(expired.TransportAttempted);
        Assert.False(expired.ExecutionAuthorized);
        Assert.False(expired.RemoteWritesPerformed);
        Assert.Equal(new[] { "Prepared", "LeaseExpired" },
            (await store.EventsAsync(packet.PreparationId, packet.PlanId,
                packet.ScopeId, prepared.Id, default)).Select(x => x.Type));
        Assert.Null(await store.BlockNextAtWriteBarrierAsync(default));
    }

    [Fact]
    public async Task New_preparation_proactively_expires_old_released_lease_slot()
    {
        var firstPacket = Packet();
        var owner = Guid.NewGuid();
        await using var db = fixture.NewContext();
        var store = Store(db);
        var leases = new PostgresGitHubTargetPreviewLeaseStore(
            db, TimeProvider.System);
        var firstLease = await leases.AcquireAsync(
            firstPacket, owner, Duration, default);
        var abandoned = await store.PrepareAsync(
            Draft(firstPacket, firstLease), default);
        Assert.True(await leases.ReleaseAsync(firstPacket.RepositoryId,
            owner, firstLease.LeaseToken, firstLease.Generation,
            firstPacket.EvidenceSha256, default));

        var nextPacket = firstPacket with {
            ScopeId = Guid.NewGuid(), EvidenceSha256 = Sha('e')
        };
        var nextOwner = Guid.NewGuid();
        var nextLease = await leases.AcquireAsync(
            nextPacket, nextOwner, Duration, default);
        // Even without Jobs:Enabled, the new prepare performs stale-lease
        // expiry under the same lock before testing the active target index.
        var next = await store.PrepareAsync(
            Draft(nextPacket, nextLease), default);
        Assert.NotEqual(abandoned.Id, next.Id);
        Assert.Equal(GitHubTransferAttemptStates.Expired,
            (await store.GetAsync(firstPacket.PreparationId,
                firstPacket.PlanId, firstPacket.ScopeId,
                abandoned.Id, default))!.State);
        Assert.Equal(GitHubTransferAttemptStates.AwaitingIndependentReview,
            next.State);
        Assert.True(await leases.ReleaseAsync(nextPacket.RepositoryId,
            nextOwner, nextLease.LeaseToken, nextLease.Generation,
            nextPacket.EvidenceSha256, default));
    }

    private PostgresGitHubTransferAttemptStore Store(
        AppDbContext db, TimeProvider? clock = null) =>
        new(db, clock ?? TimeProvider.System);

    private static string Sha(char c) => new(c, 64);

    private static GitHubCreateOnlyPacket Packet() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Random.Shared.NextInt64(1_000_000_000_000, 9_000_000_000_000),
            "gh-org", "sandbox", Sha('1'), Sha('2'), Sha('3'),
            Sha('4'), Sha('5'), Sha('6'), Sha('7'), Sha('8'),
            Sha('9'), Sha('a'), Sha('b'), 1, 0, 0,
            GitHubCreateOnlyPacketIntegrity.State,
            false, false, false, false);

    private static GitHubCandidatePolicyReview Policy(
        GitHubCreateOnlyPacket packet) =>
        new(packet.ScopeId, packet.RepositoryId, packet.EvidenceSha256,
            Sha('c'), DateTimeOffset.UtcNow, 1, 1, 0, 0,
            "AdvisoryNoRulesReported", [
                new(Sha('d'), "Branch",
                    GitHubCandidatePolicyStates.NoActiveRulesReported, 0, "Observed")
            ], false, false, false, false);

    private static GitHubTransferAttemptDraft Draft(
        GitHubCreateOnlyPacket packet, GitHubTargetPreviewLeaseClaim lease) =>
        GitHubTransferAttemptIntegrity.Build(packet, Policy(packet),
            lease.LeaseToken, lease.Generation, lease.ActorId, DateTimeOffset.UtcNow);

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
