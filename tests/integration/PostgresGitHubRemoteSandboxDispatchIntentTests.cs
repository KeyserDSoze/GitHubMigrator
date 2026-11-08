using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Real PostgreSQL 17, the complete F-075/F-077/F-078 predecessor
// chain, and multi-process-style independent DbContext transactions.
// No external GitHub sender or synthetic successful transfer.
public sealed class PostgresGitHubRemoteSandboxDispatchIntentTests(
    PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        using var services = new ServiceCollection()
            .AddDbContext<AppDbContext>(o =>
                o.UseNpgsql(fixture.ConnectionString))
            .BuildServiceProvider();
        await GitHubTargetPreviewLeaseSchema.InitializeAsync(services);
        await GitHubTransferAttemptSchema.InitializeAsync(services);
        await GitHubRemoteSandboxFenceSchema.InitializeAsync(services);
        await GitHubRemoteTestGrantSchema.InitializeAsync(services);
        await GitHubRemoteReadbackSchema.InitializeAsync(services);
        await GitHubRemoteTransmitHazardSchema.InitializeAsync(services);
        await GitHubRemoteAppPreflightEvidenceSchema.InitializeAsync(services);
        await GitHubRemoteSandboxReviewSessionSchema.InitializeAsync(services);
        await GitHubRemoteSandboxDispatchIntentSchema.InitializeAsync(services);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static GitHubRemoteTransmitHazardOptions Options(
        PostgresGitHubRemoteDispatchReviewTests.TestCase e) =>
        new(true, e.RepositoryId, "gh-org/sandbox");

    private static PostgresGitHubRemoteSandboxDispatchIntentJournal Journal(
        AppDbContext db, PostgresGitHubRemoteDispatchReviewTests.TestCase e) =>
        new(db, Options(e),
            new PostgresGitHubRemoteSandboxReviewSessionJournal(db, Options(e)));

    private static async Task<GitHubRemoteSandboxReviewSessionReceipt>
        SeedSessionAsync(AppDbContext db,
            PostgresGitHubRemoteDispatchReviewTests.TestCase e)
    {
        await PostgresGitHubRemoteDispatchReviewTests.SeedAsync(db, e);
        await new PostgresGitHubRemoteAppPreflightEvidenceJournal(
            db, new(true, e.RepositoryId, "gh-org/sandbox"))
            .RecordAsync(e.Preflight, e.Preflight.ObservedAtUtc, default);
        var review = await e.Verify(db);
        return await new PostgresGitHubRemoteSandboxReviewSessionJournal(
            db, Options(e)).RecordAsync(review, e.Actor, default);
    }

    [Fact]
    public async Task F080_integrity_scope_independent_actor_and_idempotency()
    {
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        await using var db = fixture.NewContext();
        var session = await SeedSessionAsync(db, e);
        var journal = Journal(db, e);
        var p = e.Source.Packet;
        var actor = Guid.NewGuid();
        var intent = await journal.RecordAsync(session.Id,
            p.PreparationId, p.PlanId, p.ScopeId, actor, default);

        Assert.Equal(session.Id, intent.SessionId);
        Assert.Equal(session.EvidenceSha256, intent.SessionEvidenceSha256);
        Assert.Equal(session.DispatchReviewSha256, intent.DispatchReviewSha256);
        Assert.Equal(session.PacketSha256, intent.PacketSha256);
        Assert.True(intent.ExpiresAtUtc <= session.ExpiresAtUtc);
        Assert.Equal(GitHubRemoteSandboxDispatchIntentIntegrity.Recorded,
            intent.State);
        GitHubRemoteSandboxDispatchIntentIntegrity.Verify(intent);
        Assert.False(intent.CanExecute);
        Assert.False(intent.TransportInvoked);
        Assert.False(intent.MayAutomaticallyRetry);
        Assert.False(intent.MigrationComplete);

        var replay = await journal.RecordAsync(session.Id,
            p.PreparationId, p.PlanId, p.ScopeId, actor, default);
        Assert.Equal(intent.Id, replay.Id);
        Assert.Equal(intent.EvidenceSha256, replay.EvidenceSha256);
        var current = await journal.InspectScopedAsync(
            intent.Id, p.PreparationId, p.PlanId, p.ScopeId, default);
        Assert.NotNull(current);
        Assert.Equal(GitHubRemoteSandboxDispatchIntentIntegrity.Current,
            current.Status);
        Assert.False(current.CanExecute);
        Assert.False(current.CanRetry);
        Assert.False(current.MigrationComplete);
        Assert.Null(await journal.InspectScopedAsync(
            intent.Id, Guid.NewGuid(), p.PlanId, p.ScopeId, default));
        Assert.Null(await journal.InspectScopedAsync(
            intent.Id, p.PreparationId, Guid.NewGuid(), p.ScopeId, default));
        Assert.Null(await journal.InspectScopedAsync(
            intent.Id, p.PreparationId, p.PlanId, Guid.NewGuid(), default));

        Assert.Single(await db.GitHubRemoteSandboxDispatchIntents
            .AsNoTracking().Where(x => x.SessionId == session.Id)
            .ToArrayAsync());
        Assert.Single(await db.GitHubRemoteSandboxDispatchIntentEvents
            .AsNoTracking().Where(x => x.IntentId == intent.Id)
            .ToArrayAsync());

        var conflict = await Assert.ThrowsAsync<
            GitHubRemoteSandboxDispatchIntentException>(() =>
            journal.RecordAsync(session.Id, p.PreparationId, p.PlanId,
                p.ScopeId, Guid.NewGuid(), default));
        Assert.Equal("SandboxDispatchIntentConflict", conflict.Code);
    }

    [Fact]
    public async Task F080_six_replicas_contend_and_write_exactly_one_immutable_intent()
    {
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        GitHubRemoteSandboxReviewSessionReceipt session;
        await using (var setup = fixture.NewContext())
            session = await SeedSessionAsync(setup, e);
        var p = e.Source.Packet;
        var actor = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(async _ =>
            {
                await using var db = fixture.NewContext();
                return await Journal(db, e).RecordAsync(session.Id,
                    p.PreparationId, p.PlanId, p.ScopeId, actor, default);
            }));
        Assert.All(results, x =>
        {
            Assert.Equal(results[0].Id, x.Id);
            Assert.Equal(results[0].EvidenceSha256, x.EvidenceSha256);
            Assert.False(x.CanExecute);
            Assert.False(x.MayAutomaticallyRetry);
        });
        await using var check = fixture.NewContext();
        Assert.Single(await check.GitHubRemoteSandboxDispatchIntents
            .AsNoTracking().Where(x => x.SessionId == session.Id)
            .ToArrayAsync());
        Assert.Single(await check.GitHubRemoteSandboxDispatchIntentEvents
            .AsNoTracking().Where(x => x.IntentId == results[0].Id)
            .ToArrayAsync());
    }

    [Fact]
    public async Task F080_may_have_transmitted_blocks_record_and_makes_existing_historical()
    {
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        await using var db = fixture.NewContext();
        var session = await SeedSessionAsync(db, e);
        var p = e.Source.Packet;
        var actor = Guid.NewGuid();
        var journal = Journal(db, e);
        var intent = await journal.RecordAsync(session.Id,
            p.PreparationId, p.PlanId, p.ScopeId, actor, default);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE github_remote_transmit_hazards
            SET state='MayHaveTransmitted',marked_by={Guid.NewGuid()},
                updated_at_utc=clock_timestamp()
            WHERE id={e.HazardId}
            """);
        var view = await journal.InspectScopedAsync(intent.Id,
            p.PreparationId, p.PlanId, p.ScopeId, default);
        Assert.NotNull(view);
        Assert.Equal(GitHubRemoteSandboxDispatchIntentIntegrity.Historical,
            view.Status);
        Assert.True(view.HistoricalOnly);
        Assert.False(view.CanExecute);
        Assert.False(view.CanRetry);
        var refused = await Assert.ThrowsAsync<
            GitHubRemoteSandboxDispatchIntentException>(() =>
                journal.RecordAsync(session.Id, p.PreparationId,
                    p.PlanId, p.ScopeId, actor, default));
        Assert.Equal("SandboxDispatchIntentSessionStale", refused.Code);
        Assert.Equal(1, await db.GitHubRemoteSandboxDispatchIntentEvents
            .AsNoTracking().CountAsync(x => x.IntentId == intent.Id));
    }

    [Fact]
    public async Task F080_sql_triggers_refuse_intent_and_event_mutation()
    {
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        await using var db = fixture.NewContext();
        var session = await SeedSessionAsync(db, e);
        var p = e.Source.Packet;
        var journal = Journal(db, e);
        var intent = await journal.RecordAsync(session.Id,
            p.PreparationId, p.PlanId, p.ScopeId, Guid.NewGuid(), default);
        foreach (var sql in new[] {
            "UPDATE github_remote_sandbox_dispatch_intents SET can_execute=true WHERE id=@id",
            "UPDATE github_remote_sandbox_dispatch_intents SET evidence_sha256='ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff' WHERE id=@id",
            "DELETE FROM github_remote_sandbox_dispatch_intents WHERE id=@id",
            "DELETE FROM github_remote_sandbox_dispatch_intent_events WHERE intent_id=@id"
        })
            await Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.ExecuteSqlRawAsync(sql,
                    new NpgsqlParameter("id", intent.Id)));

        var view = await journal.InspectScopedAsync(intent.Id,
            p.PreparationId, p.PlanId, p.ScopeId, default);
        Assert.NotNull(view);
        Assert.Equal(intent.EvidenceSha256, view.Intent.EvidenceSha256);
    }

    [Fact]
    public async Task F080_repository_lock_refuses_preemptive_possible_send_marker()
    {
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        GitHubRemoteSandboxReviewSessionReceipt session;
        await using (var setup = fixture.NewContext())
            session = await SeedSessionAsync(setup, e);
        var p = e.Source.Packet;
        await using var blocker = fixture.NewContext();
        await using var tx = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(733050, hashtext({e.RepositoryId.ToString()}))");
        var pending = Task.Run(async () =>
        {
            await using var db = fixture.NewContext();
            return await Journal(db, e).RecordAsync(session.Id,
                p.PreparationId, p.PlanId, p.ScopeId, Guid.NewGuid(), default);
        });
        await Task.Delay(140);
        Assert.False(pending.IsCompleted);
        await blocker.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE github_remote_transmit_hazards
            SET state='MayHaveTransmitted',marked_by={Guid.NewGuid()},
                updated_at_utc=clock_timestamp()
            WHERE id={e.HazardId}
            """);
        await tx.CommitAsync();
        var error = await Assert.ThrowsAsync<
            GitHubRemoteSandboxDispatchIntentException>(async () =>
                await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("SandboxDispatchIntentSessionStale", error.Code);
        await using var check = fixture.NewContext();
        Assert.Empty(await check.GitHubRemoteSandboxDispatchIntents
            .AsNoTracking().Where(x => x.SessionId == session.Id)
            .ToArrayAsync());
    }
}
