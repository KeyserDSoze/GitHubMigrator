using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Real PostgreSQL 17, independent contexts as independent ASP.NET replicas.
// No GitHub network calls and no repository-mutating Git operations.
public sealed class PostgresGitHubTargetPreviewLeaseTests(
    PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        // EnsureCreated creates EF tables but does not create all custom
        // constraints. Exercise the production additive SQL CHECK schema.
        using var services = new ServiceCollection()
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(fixture.ConnectionString))
            .BuildServiceProvider();
        await GitHubTargetPreviewLeaseSchema.InitializeAsync(services);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Sha(char c) => new(c, 64);

    private static GitHubCreateOnlyPacket Packet(long repositoryId, char proof = 'a') =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), repositoryId,
            "gh-org", "sandbox", Sha('1'), Sha('2'), Sha('3'), Sha('4'),
            Sha('5'), Sha('6'), Sha('7'), Sha('8'), Sha('9'), Sha('b'),
            Sha(proof), 1, 0, 0, GitHubCreateOnlyPacketIntegrity.State,
            false, false, false, false);

    private static long RepoId() =>
        // Unique positive GitHub-like IDs prevent cross-test contamination.
        Random.Shared.NextInt64(1_000_000_000_000, 8_000_000_000_000);

    private PostgresGitHubTargetPreviewLeaseStore Store(
        AppDbContext db, TimeProvider? time = null) =>
        new(db, time ?? TimeProvider.System);

    [Fact]
    public async Task Only_one_of_concurrent_replicas_can_hold_a_target()
    {
        var packet = Packet(RepoId());
        var actors = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        async Task<GitHubTargetPreviewLeaseClaim?> Attempt(Guid actor)
        {
            await using var db = fixture.NewContext();
            try
            {
                return await Store(db).AcquireAsync(
                    packet, actor, TimeSpan.FromSeconds(15), default);
            }
            catch (GitHubTargetPreviewLeaseException ex)
                when (ex.Code == "PreviewLeaseBusy")
            { return null; }
        }

        var results = await Task.WhenAll(actors.Select(Attempt));
        var winner = Assert.Single(results, x => x is not null)!;
        Assert.Equal(1, winner.Generation);
        Assert.False(winner.ExecutionAuthorized);
        Assert.False(winner.RemoteWritesPerformed);
        Assert.False(winner.ReadyForExecution);
        await using var verify = fixture.NewContext();
        var store = Store(verify);
        var status = await store.GetAsync(packet.RepositoryId, default);
        Assert.Equal(GitHubTargetPreviewLeaseIntegrity.Held, status!.State);
        Assert.DoesNotContain("leaseToken", System.Text.Json.JsonSerializer.Serialize(status),
            StringComparison.OrdinalIgnoreCase);
        Assert.Single(await store.EventsAsync(packet.RepositoryId, default),
            x => x.Type == "Acquired");
        Assert.True(await store.ReleaseAsync(packet.RepositoryId,
            winner.ActorId, winner.LeaseToken, winner.Generation,
            winner.PacketSha256, default));
    }

    [Fact]
    public async Task Wrong_owner_token_or_generation_can_never_renew_or_release()
    {
        var packet = Packet(RepoId());
        var actor = Guid.NewGuid();
        await using var db = fixture.NewContext();
        var store = Store(db);
        var claim = await store.AcquireAsync(
            packet, actor, TimeSpan.FromSeconds(15), default);
        Assert.False(await store.ReleaseAsync(packet.RepositoryId, Guid.NewGuid(),
            claim.LeaseToken, claim.Generation, packet.EvidenceSha256, default));
        Assert.False(await store.ReleaseAsync(packet.RepositoryId, actor,
            Guid.NewGuid(), claim.Generation, packet.EvidenceSha256, default));
        Assert.False(await store.ReleaseAsync(packet.RepositoryId, actor,
            claim.LeaseToken, claim.Generation + 1, packet.EvidenceSha256, default));
        Assert.False(await store.RenewAsync(packet, actor, claim.LeaseToken,
            claim.Generation + 1, TimeSpan.FromSeconds(10), default));
        Assert.True(await store.RenewAsync(packet, actor, claim.LeaseToken,
            claim.Generation, TimeSpan.FromSeconds(10), default));
        Assert.True(await store.ReleaseAsync(packet.RepositoryId, actor,
            claim.LeaseToken, claim.Generation, packet.EvidenceSha256, default));
        Assert.False(await store.ReleaseAsync(packet.RepositoryId, actor,
            claim.LeaseToken, claim.Generation, packet.EvidenceSha256, default));

        var second = await store.AcquireAsync(
            packet, Guid.NewGuid(), TimeSpan.FromSeconds(10), default);
        Assert.Equal(2, second.Generation);
        Assert.NotEqual(claim.LeaseToken, second.LeaseToken);
        Assert.False(await store.RenewAsync(packet, actor, claim.LeaseToken,
            claim.Generation, TimeSpan.FromSeconds(10), default));
        Assert.True(await store.ReleaseAsync(packet.RepositoryId, second.ActorId,
            second.LeaseToken, second.Generation, packet.EvidenceSha256, default));
    }

    [Fact]
    public async Task Expired_claim_is_quarantined_until_different_actor_and_fresh_proof_recover()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new ControlledTime(now);
        var original = Packet(RepoId());
        var actor = Guid.NewGuid();
        var independentReviewer = Guid.NewGuid();
        await using var db = fixture.NewContext();
        var store = Store(db, clock);
        var claim = await store.AcquireAsync(original, actor,
            TimeSpan.FromSeconds(10), default);
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(GitHubTargetPreviewLeaseIntegrity.RecoveryRequired,
            (await store.GetAsync(original.RepositoryId, default))!.State);
        Assert.False(await store.ReleaseAsync(original.RepositoryId,
            actor, claim.LeaseToken, claim.Generation, claim.PacketSha256, default));
        var fresh = original with { EvidenceSha256 = Sha('f') };
        var error = await Assert.ThrowsAsync<GitHubTargetPreviewLeaseException>(
            () => store.AcquireAsync(fresh, independentReviewer,
                TimeSpan.FromSeconds(10), default));
        Assert.Equal("PreviewLeaseRecoveryRequired", error.Code);
        Assert.False(await store.RecoverAsync(fresh, actor, claim.Generation,
            claim.PacketSha256, default)); // original actor cannot self-recover
        Assert.False(await store.RecoverAsync(fresh, independentReviewer,
            claim.Generation + 1, claim.PacketSha256, default));
        Assert.True(await store.RecoverAsync(fresh, independentReviewer,
            claim.Generation, claim.PacketSha256, default));
        Assert.False(await store.RecoverAsync(fresh, independentReviewer,
            claim.Generation, claim.PacketSha256, default));
        Assert.False(await store.RenewAsync(original, actor,
            claim.LeaseToken, claim.Generation, TimeSpan.FromSeconds(10), default));
        Assert.False(await store.ReleaseAsync(original.RepositoryId,
            actor, claim.LeaseToken, claim.Generation, claim.PacketSha256, default));
        var newer = await store.AcquireAsync(fresh, independentReviewer,
            TimeSpan.FromSeconds(10), default);
        Assert.Equal(claim.Generation + 2, newer.Generation);
        Assert.True(await store.ReleaseAsync(fresh.RepositoryId,
            newer.ActorId, newer.LeaseToken, newer.Generation,
            newer.PacketSha256, default));

        var events = await store.EventsAsync(original.RepositoryId, default);
        Assert.Equal(new[] { "Acquired", "Quarantined", "Recovered", "Acquired", "Released" },
            events.Select(x => x.Type));
    }

    [Fact]
    public async Task Independent_numeric_repositories_do_not_block_each_other()
    {
        var a = Packet(RepoId());
        var b = Packet(RepoId());
        await using var first = fixture.NewContext();
        await using var second = fixture.NewContext();
        var one = await Store(first).AcquireAsync(
            a, Guid.NewGuid(), TimeSpan.FromSeconds(10), default);
        var two = await Store(second).AcquireAsync(
            b, Guid.NewGuid(), TimeSpan.FromSeconds(10), default);
        Assert.NotEqual(one.RepositoryId, two.RepositoryId);
        Assert.True(await Store(first).ReleaseAsync(a.RepositoryId, one.ActorId,
            one.LeaseToken, one.Generation, one.PacketSha256, default));
        Assert.True(await Store(second).ReleaseAsync(b.RepositoryId, two.ActorId,
            two.LeaseToken, two.Generation, two.PacketSha256, default));
    }

    [Fact]
    public async Task Sql_constraints_reject_any_remote_write_or_execution_flag()
    {
        var packet = Packet(RepoId());
        await using var db = fixture.NewContext();
        var store = Store(db);
        var claim = await store.AcquireAsync(packet, Guid.NewGuid(),
            TimeSpan.FromSeconds(10), default);
        var forbiddenWrites = new Func<Task>[] {
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE github_target_preview_leases SET execution_authorized = {true} WHERE repository_id = {packet.RepositoryId}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE github_target_preview_leases SET remote_writes_performed = {true} WHERE repository_id = {packet.RepositoryId}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE github_target_preview_leases SET ready_for_execution = {true} WHERE repository_id = {packet.RepositoryId}")
        };
        foreach (var denied in forbiddenWrites)
            await Assert.ThrowsAnyAsync<Exception>(denied);
        Assert.True(await store.ReleaseAsync(packet.RepositoryId,
            claim.ActorId, claim.LeaseToken, claim.Generation,
            claim.PacketSha256, default));
    }

    [Fact]
    public async Task Invalid_packet_and_overlong_lease_never_create_rows()
    {
        var packet = Packet(RepoId());
        await using var db = fixture.NewContext();
        var store = Store(db);
        await Assert.ThrowsAsync<GitHubTargetPreviewLeaseException>(() =>
            store.AcquireAsync(packet with { ExecutionAuthorized = true },
                Guid.NewGuid(), TimeSpan.FromSeconds(10), default));
        await Assert.ThrowsAsync<GitHubTargetPreviewLeaseException>(() =>
            store.AcquireAsync(packet, Guid.NewGuid(),
                TimeSpan.FromSeconds(21), default));
        Assert.Null(await store.GetAsync(packet.RepositoryId, default));
    }

    private sealed class ControlledTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
