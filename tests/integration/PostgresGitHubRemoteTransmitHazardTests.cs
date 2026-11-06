using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// PostgreSQL 17 exercises cross-replica CAS, expiry quarantine and
// irreversible possible-transmission state without any GitHub transport.
public sealed class PostgresGitHubRemoteTransmitHazardTests(
    PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        using var provider = new ServiceCollection()
            .AddDbContext<AppDbContext>(o =>
                o.UseNpgsql(fixture.ConnectionString))
            .BuildServiceProvider();
        await GitHubTransferAttemptSchema.InitializeAsync(provider);
        await GitHubRemoteSandboxFenceSchema.InitializeAsync(provider);
        await GitHubRemoteTestGrantSchema.InitializeAsync(provider);
        await GitHubRemoteReadbackSchema.InitializeAsync(provider);
        await GitHubRemoteTransmitHazardSchema.InitializeAsync(provider);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Six_replicas_reserve_single_epoch_and_commit_one_pessimistic_mark()
    {
        var e = Evidence();
        await using (var setup = fixture.NewContext())
            await SeedAsync(setup, e);

        async Task<(GitHubRemoteTransmitHazardClaim? Claim, string? Error)> Prepare()
        {
            await using var db = fixture.NewContext();
            try { return (await Journal(db, e).PrepareAsync(
                e.GrantId, e.RepositoryId, e.Actor, default), null); }
            catch (GitHubRemoteTransmitHazardException ex)
            { return (null, ex.Code); }
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => Prepare()));
        var claim = Assert.Single(results.Where(x => x.Claim is not null)).Claim!;
        Assert.All(results.Where(x => x.Claim is null), x =>
            Assert.Equal("RemoteHazardEpochConsumed", x.Error));
        Assert.Equal(GitHubRemoteTransmitHazardStates.PreparedWriteDisabled,
            claim.Status.State);

        await using var worker = fixture.NewContext();
        var journal = Journal(worker, e);
        Assert.False(await journal.AbortBeforeTransportAsync(
            claim.Status.Id, e.RepositoryId, Guid.NewGuid(),
            claim.AbortToken, default));
        Assert.False(await journal.AbortBeforeTransportAsync(
            claim.Status.Id, e.RepositoryId, e.Actor, Guid.NewGuid(), default));

        var marked = await journal.MarkMayHaveTransmittedAsync(
            claim.Status.Id, e.RepositoryId, e.Actor, claim.AbortToken, default);
        Assert.Equal(GitHubRemoteTransmitHazardStates.MayHaveTransmitted, marked.State);
        Assert.False(marked.TransportAuthorized);
        Assert.False(marked.TransportInvoked);
        Assert.False(marked.RemoteWritesPerformed);
        Assert.False(marked.MayAutomaticallyRetry);
        Assert.False(await journal.AbortBeforeTransportAsync(
            claim.Status.Id, e.RepositoryId, e.Actor, claim.AbortToken, default));

        var ex = await Assert.ThrowsAsync<GitHubRemoteTransmitHazardException>(
            () => journal.MarkMayHaveTransmittedAsync(
                claim.Status.Id, e.RepositoryId, e.Actor, claim.AbortToken, default));
        Assert.Equal("RemoteHazardStaleOrAlreadyMarked", ex.Code);

        await using var restarted = fixture.NewContext();
        var status = await Journal(restarted, e).GetAsync(
            claim.Status.Id, e.RepositoryId, default);
        Assert.NotNull(status);
        Assert.Equal(GitHubRemoteTransmitHazardStates.MayHaveTransmitted,
            status.State);
        var events = await Journal(restarted, e).EventsAsync(
            claim.Status.Id, e.RepositoryId, default);
        Assert.Equal(new[] { "PreparedWriteDisabled", "MayHaveTransmitted" },
            events.Select(x => x.Type));
        Assert.Single(await restarted.GitHubRemoteTransmitHazards.AsNoTracking()
            .Where(x => x.GrantId == e.GrantId).ToArrayAsync());
        Assert.DoesNotContain(claim.AbortToken.ToString("D"),
            System.Text.Json.JsonSerializer.Serialize(status));
    }

    [Fact]
    public async Task Concurrent_abort_vs_mark_can_only_choose_one_terminal_state()
    {
        var e = Evidence();
        await using (var db = fixture.NewContext())
            await SeedAsync(db, e);
        GitHubRemoteTransmitHazardClaim claim;
        await using (var db = fixture.NewContext())
            claim = await Journal(db, e).PrepareAsync(
                e.GrantId, e.RepositoryId, e.Actor, default);

        async Task<string> Mark()
        {
            await using var db = fixture.NewContext();
            try
            {
                var row = await Journal(db, e).MarkMayHaveTransmittedAsync(
                    claim.Status.Id, e.RepositoryId, e.Actor,
                    claim.AbortToken, default);
                return row.State;
            }
            catch (GitHubRemoteTransmitHazardException)
            { return "LostMark"; }
        }
        async Task<string> Abort()
        {
            await using var db = fixture.NewContext();
            return await Journal(db, e).AbortBeforeTransportAsync(
                claim.Status.Id, e.RepositoryId, e.Actor,
                claim.AbortToken, default) ? "AbortedBeforeTransport" : "LostAbort";
        }
        var outcome = await Task.WhenAll(Mark(), Abort());
        Assert.True(outcome.Contains("MayHaveTransmitted") ^
                    outcome.Contains("AbortedBeforeTransport"));

        await using var verify = fixture.NewContext();
        var row = (await Journal(verify, e).GetAsync(
            claim.Status.Id, e.RepositoryId, default))!;
        Assert.Contains(row.State, new[] {
            GitHubRemoteTransmitHazardStates.MayHaveTransmitted,
            GitHubRemoteTransmitHazardStates.AbortedBeforeTransport
        });
        Assert.Equal(2, (await Journal(verify, e).EventsAsync(
            claim.Status.Id, e.RepositoryId, default)).Count);
    }

    [Fact]
    public async Task Expired_prepared_hazard_quarantines_once_and_never_resends()
    {
        var e = Evidence();
        await using var db = fixture.NewContext();
        await SeedAsync(db, e);
        var clock = new AdjustableClock(e.Now);
        var journal = Journal(db, e, clock);
        var claim = await journal.PrepareAsync(
            e.GrantId, e.RepositoryId, e.Actor, default);
        clock.Advance(TimeSpan.FromMinutes(3));
        var ex = await Assert.ThrowsAsync<GitHubRemoteTransmitHazardException>(
            () => journal.MarkMayHaveTransmittedAsync(
                claim.Status.Id, e.RepositoryId, e.Actor,
                claim.AbortToken, default));
        Assert.Equal("RemoteHazardRecoveryRequired", ex.Code);
        Assert.Equal(GitHubRemoteTransmitHazardStates.OperatorReviewRequired,
            (await journal.GetAsync(claim.Status.Id, e.RepositoryId, default))!.State);
        Assert.False(await journal.AbortBeforeTransportAsync(
            claim.Status.Id, e.RepositoryId, e.Actor,
            claim.AbortToken, default));
        Assert.False(await journal.QuarantineExpiredAsync(default));
        Assert.Equal(1, (await journal.EventsAsync(
            claim.Status.Id, e.RepositoryId, default))
            .Count(x => x.Type == "OperatorReviewRequired"));
        Assert.False((await journal.GetAsync(
            claim.Status.Id, e.RepositoryId, default))!.MayAutomaticallyRetry);
    }

    [Fact]
    public async Task Changed_F050_and_attempted_direct_SQL_authority_fail_closed()
    {
        var e = Evidence();
        await using var db = fixture.NewContext();
        await SeedAsync(db, e);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE github_remote_sandbox_fences SET state='Released'
            WHERE repository_id={e.RepositoryId}
            """);
        var bad = await Assert.ThrowsAsync<GitHubRemoteTransmitHazardException>(
            () => Journal(db, e).PrepareAsync(
                e.GrantId, e.RepositoryId, e.Actor, default));
        Assert.Equal("RemoteTransmitHazardBarrierChanged", bad.Code);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE github_remote_sandbox_fences SET state='ReviewedWriteDisabled'
            WHERE repository_id={e.RepositoryId}
            """);
        var claim = await Journal(db, e).PrepareAsync(
            e.GrantId, e.RepositoryId, e.Actor, default);

        Func<Task>[] forbidden = [
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE github_remote_transmit_hazards SET transport_authorized={true} WHERE id={claim.Status.Id}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE github_remote_transmit_hazards SET transport_invoked={true} WHERE id={claim.Status.Id}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE github_remote_transmit_hazards SET remote_writes_performed={true} WHERE id={claim.Status.Id}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE github_remote_transmit_hazards SET may_automatically_retry={true} WHERE id={claim.Status.Id}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE github_remote_transmit_hazards SET packet_sha256={"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"} WHERE id={claim.Status.Id}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM github_remote_transmit_hazard_events WHERE hazard_id={claim.Status.Id}")
        ];
        foreach (var change in forbidden)
            await Assert.ThrowsAnyAsync<Exception>(change);
        Assert.Equal(GitHubRemoteTransmitHazardStates.PreparedWriteDisabled,
            (await Journal(db, e).GetAsync(
                claim.Status.Id, e.RepositoryId, default))!.State);
    }

    private sealed record Seed(Guid GrantId, long RepositoryId,
        Guid Actor, DateTimeOffset Now);

    private static Seed Evidence() =>
        new(Guid.NewGuid(),
            Random.Shared.NextInt64(100_000_000_000L, 900_000_000_000L),
            Guid.NewGuid(), DateTimeOffset.UtcNow);

    private static string Sha(char c) => new(c, 64);

    private static PostgresGitHubRemoteTransmitHazardJournal Journal(
        AppDbContext db, Seed seed, TimeProvider? clock = null) =>
        new(db, clock ?? TimeProvider.System,
            new(true, seed.RepositoryId, "gh-org/test-sandbox"));

    private static async Task SeedAsync(AppDbContext db, Seed e)
    {
        var now = e.Now;
        var attempt = Guid.NewGuid();
        var request = Guid.NewGuid();
        var prep = Guid.NewGuid();
        var plan = Guid.NewGuid();
        var scope = Guid.NewGuid();
        var packet = Sha('a');
        var candidate = Sha('b');
        var policy = Sha('c');
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_transfer_attempts (
                id, preparation_id, plan_id, scope_id, repository_id,
                organization, repository, packet_sha256, policy_sha256,
                candidate_set_sha256, candidate_count, policy_observed_at_utc,
                lease_token, lease_generation, requested_by, reviewed_by,
                created_at_utc, reviewed_at_utc, blocked_at_utc, expired_at_utc,
                state, transport_attempted, execution_authorized,
                remote_writes_performed, ready_for_execution)
            VALUES ({attempt},{prep},{plan},{scope},{e.RepositoryId},
                {"gh-org"},{"test-sandbox"},{packet},{policy},{candidate},
                {1},{now},{Guid.NewGuid()},{1L},
                {Guid.NewGuid()},{Guid.NewGuid()},{now},{now},{now},NULL,
                'BlockedAtWriteBarrier',false,false,false,false)
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_remote_sandbox_fences (
                repository_id, request_id, preparation_id, plan_id,
                scope_id, attempt_id, organization, repository,
                packet_sha256, candidate_set_sha256, policy_sha256,
                token_sha256, generation, state, requested_by,
                reviewed_by, deadline_utc, updated_at_utc,
                execution_authorized, remote_writes_performed, ready_for_execution)
            VALUES ({e.RepositoryId},{request},{prep},{plan},{scope},{attempt},
                {"gh-org"},{"test-sandbox"},{packet},{candidate},{policy},
                {Sha('d')},{1L},'ReviewedWriteDisabled',
                {Guid.NewGuid()},{Guid.NewGuid()},{now.AddMinutes(2)},{now},
                false,false,false)
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_remote_test_grants (
                id, repository_id, organization, repository,
                preparation_id, plan_id, scope_id, attempt_id,
                fence_request_id, fence_generation, packet_sha256,
                candidate_set_sha256, original_policy_sha256,
                baseline_sha256, preflight_sha256, cancellation_token_sha256,
                state, requested_by, reviewed_by, deadline_utc,
                updated_at_utc, execution_authorized, remote_writes_performed,
                ready_for_execution)
            VALUES ({e.GrantId},{e.RepositoryId},{"gh-org"},{"test-sandbox"},
                {prep},{plan},{scope},{attempt},{request},{1L},
                {packet},{candidate},{policy},{Sha('e')},{Sha('f')},
                {Sha('1')},'ReviewedWriteDisabled',
                {Guid.NewGuid()},{Guid.NewGuid()},{now.AddMinutes(2)},{now},
                false,false,false)
            """);
    }

    private sealed class AdjustableClock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset now = initial;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan time) => now = now.Add(time);
    }
}
