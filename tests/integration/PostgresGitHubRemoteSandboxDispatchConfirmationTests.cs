using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// End-to-end F-075/077/078/080/081 on real PostgreSQL 17. No GitHub
// mutation, transport command, sender or replayable execution capability.
public sealed class PostgresGitHubRemoteSandboxDispatchConfirmationTests(
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
        await GitHubRemoteSandboxDispatchConfirmationSchema.InitializeAsync(services);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static GitHubRemoteTransmitHazardOptions Options(
        PostgresGitHubRemoteDispatchReviewTests.TestCase e) =>
        new(true, e.RepositoryId, "gh-org/sandbox");

    private static PostgresGitHubRemoteSandboxDispatchIntentJournal Intents(
        AppDbContext db, PostgresGitHubRemoteDispatchReviewTests.TestCase e) =>
        new(db, Options(e),
            new PostgresGitHubRemoteSandboxReviewSessionJournal(db, Options(e)));

    private static PostgresGitHubRemoteSandboxDispatchConfirmationJournal Journal(
        AppDbContext db, PostgresGitHubRemoteDispatchReviewTests.TestCase e) =>
        new(db, Options(e), Intents(db, e),
            new PostgresGitHubRemoteSandboxReviewSessionJournal(db, Options(e)));

    private static async Task<GitHubRemoteSandboxDispatchIntentReceipt>
        SeedAsync(AppDbContext db, PostgresGitHubRemoteDispatchReviewTests.TestCase e,
            Guid proposer)
    {
        await PostgresGitHubRemoteDispatchReviewTests.SeedAsync(db, e);
        await new PostgresGitHubRemoteAppPreflightEvidenceJournal(
            db, Options(e)).RecordAsync(
                e.Preflight, e.Preflight.ObservedAtUtc, default);
        var review = await e.Verify(db);
        var session = await new PostgresGitHubRemoteSandboxReviewSessionJournal(
            db, Options(e)).RecordAsync(review, e.Actor, default);
        var p = e.Source.Packet;
        return await Intents(db, e).RecordAsync(session.Id,
            p.PreparationId, p.PlanId, p.ScopeId, proposer, default);
    }

    [Fact]
    public async Task F081_independent_confirmation_is_hash_sealed_scoped_and_idempotent()
    {
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        await using var db = fixture.NewContext();
        var proposer = Guid.NewGuid();
        var intent = await SeedAsync(db, e, proposer);
        var p = e.Source.Packet;
        var journal = Journal(db, e);
        var confirmer = Guid.NewGuid();

        var sameActor = await Assert.ThrowsAsync<
            GitHubRemoteSandboxDispatchConfirmationException>(() =>
                journal.ConfirmAsync(intent.Id, p.PreparationId, p.PlanId,
                    p.ScopeId, proposer, default));
        Assert.Equal("SandboxDispatchConfirmationIntentStale", sameActor.Code);
        Assert.Empty(await db.GitHubRemoteSandboxDispatchConfirmations
            .AsNoTracking().Where(x => x.IntentId == intent.Id).ToArrayAsync());

        var confirmation = await journal.ConfirmAsync(intent.Id,
            p.PreparationId, p.PlanId, p.ScopeId, confirmer, default);
        GitHubRemoteSandboxDispatchConfirmationIntegrity.Verify(confirmation);
        Assert.Equal(intent.Id, confirmation.IntentId);
        Assert.Equal(intent.EvidenceSha256, confirmation.IntentEvidenceSha256);
        Assert.Equal(intent.SessionEvidenceSha256,
            confirmation.SessionEvidenceSha256);
        Assert.True(confirmation.ExpiresAtUtc <= intent.ExpiresAtUtc);
        Assert.Equal(GitHubRemoteSandboxDispatchConfirmationIntegrity.Recorded,
            confirmation.State);
        Assert.False(confirmation.CanDispatch);
        Assert.False(confirmation.TransportInvoked);
        Assert.False(confirmation.MayAutomaticallyRetry);
        Assert.False(confirmation.MigrationComplete);

        var again = await journal.ConfirmAsync(intent.Id,
            p.PreparationId, p.PlanId, p.ScopeId, confirmer, default);
        Assert.Equal(confirmation.Id, again.Id);
        Assert.Equal(confirmation.EvidenceSha256, again.EvidenceSha256);
        var view = await journal.InspectScopedAsync(
            confirmation.Id, intent.Id,
            p.PreparationId, p.PlanId, p.ScopeId, default);
        Assert.NotNull(view);
        Assert.Equal(GitHubRemoteSandboxDispatchConfirmationIntegrity.Current,
            view.Status);
        Assert.False(view.CanDispatch);
        Assert.False(view.CanRetry);
        Assert.False(view.MigrationComplete);
        Assert.Null(await journal.InspectScopedAsync(
            confirmation.Id, intent.Id,
            Guid.NewGuid(), p.PlanId, p.ScopeId, default));
        Assert.Null(await journal.InspectScopedAsync(
            confirmation.Id, intent.Id,
            p.PreparationId, Guid.NewGuid(), p.ScopeId, default));
        Assert.Null(await journal.InspectScopedAsync(
            confirmation.Id, intent.Id,
            p.PreparationId, p.PlanId, Guid.NewGuid(), default));
        Assert.Null(await journal.InspectScopedAsync(
            confirmation.Id, Guid.NewGuid(),
            p.PreparationId, p.PlanId, p.ScopeId, default));

        var alternate = await Assert.ThrowsAsync<
            GitHubRemoteSandboxDispatchConfirmationException>(() =>
                journal.ConfirmAsync(intent.Id, p.PreparationId, p.PlanId,
                    p.ScopeId, Guid.NewGuid(), default));
        Assert.Equal("SandboxDispatchConfirmationConflict", alternate.Code);
        Assert.Single(await db.GitHubRemoteSandboxDispatchConfirmations
            .AsNoTracking().Where(x => x.IntentId == intent.Id).ToArrayAsync());
        Assert.Single(await db.GitHubRemoteSandboxDispatchConfirmationEvents
            .AsNoTracking().Where(x => x.ConfirmationId == confirmation.Id)
            .ToArrayAsync());
    }

    [Fact]
    public async Task F081_six_replicas_confirm_one_record_and_one_event()
    {
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        GitHubRemoteSandboxDispatchIntentReceipt intent;
        var proposer = Guid.NewGuid();
        await using (var setup = fixture.NewContext())
            intent = await SeedAsync(setup, e, proposer);
        var p = e.Source.Packet;
        var actor = Guid.NewGuid();
        var parallel = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(async _ =>
            {
                await using var db = fixture.NewContext();
                return await Journal(db, e).ConfirmAsync(intent.Id,
                    p.PreparationId, p.PlanId, p.ScopeId, actor, default);
            }));
        Assert.All(parallel, x =>
        {
            Assert.Equal(parallel[0].Id, x.Id);
            Assert.Equal(parallel[0].EvidenceSha256, x.EvidenceSha256);
            Assert.False(x.CanDispatch);
            Assert.False(x.MayAutomaticallyRetry);
        });
        await using var check = fixture.NewContext();
        Assert.Single(await check.GitHubRemoteSandboxDispatchConfirmations
            .AsNoTracking().Where(x => x.IntentId == intent.Id).ToArrayAsync());
        Assert.Single(await check.GitHubRemoteSandboxDispatchConfirmationEvents
            .AsNoTracking().Where(x => x.ConfirmationId == parallel[0].Id)
            .ToArrayAsync());
    }

    [Fact]
    public async Task F081_may_have_transmitted_demotes_confirmation_and_blocks_replay()
    {
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        await using var db = fixture.NewContext();
        var intent = await SeedAsync(db, e, Guid.NewGuid());
        var p = e.Source.Packet;
        var actor = Guid.NewGuid();
        var journal = Journal(db, e);
        var confirmation = await journal.ConfirmAsync(intent.Id,
            p.PreparationId, p.PlanId, p.ScopeId, actor, default);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE github_remote_transmit_hazards
            SET state='MayHaveTransmitted',marked_by={Guid.NewGuid()},
                updated_at_utc=clock_timestamp()
            WHERE id={e.HazardId}
            """);
        var historical = await journal.InspectScopedAsync(
            confirmation.Id, intent.Id,
            p.PreparationId, p.PlanId, p.ScopeId, default);
        Assert.NotNull(historical);
        Assert.Equal(GitHubRemoteSandboxDispatchConfirmationIntegrity.Historical,
            historical.Status);
        Assert.True(historical.HistoricalOnly);
        Assert.False(historical.CanDispatch);
        Assert.False(historical.CanRetry);
        Assert.False(historical.MigrationComplete);
        var refused = await Assert.ThrowsAsync<
            GitHubRemoteSandboxDispatchConfirmationException>(() =>
                journal.ConfirmAsync(intent.Id, p.PreparationId,
                    p.PlanId, p.ScopeId, actor, default));
        Assert.Equal("SandboxDispatchConfirmationIntentStale", refused.Code);
    }

    [Fact]
    public async Task F081_sql_triggers_forbid_approval_tampering_or_deletion()
    {
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        await using var db = fixture.NewContext();
        var intent = await SeedAsync(db, e, Guid.NewGuid());
        var p = e.Source.Packet;
        var journal = Journal(db, e);
        var confirmation = await journal.ConfirmAsync(intent.Id,
            p.PreparationId, p.PlanId, p.ScopeId, Guid.NewGuid(), default);
        foreach (var sql in new[] {
            "UPDATE github_remote_sandbox_dispatch_confirmations SET can_dispatch=true WHERE id=@id",
            "UPDATE github_remote_sandbox_dispatch_confirmations SET evidence_sha256='ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff' WHERE id=@id",
            "DELETE FROM github_remote_sandbox_dispatch_confirmations WHERE id=@id",
            "DELETE FROM github_remote_sandbox_dispatch_confirmation_events WHERE confirmation_id=@id"
        })
            await Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.ExecuteSqlRawAsync(sql,
                    new NpgsqlParameter("id", confirmation.Id)));
        Assert.NotNull(await journal.InspectScopedAsync(
            confirmation.Id, intent.Id,
            p.PreparationId, p.PlanId, p.ScopeId, default));
    }

    [Fact]
    public async Task F081_competing_f055_possible_send_marker_wins_locked_confirmation()
    {
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        GitHubRemoteSandboxDispatchIntentReceipt intent;
        await using (var setup = fixture.NewContext())
            intent = await SeedAsync(setup, e, Guid.NewGuid());
        var p = e.Source.Packet;
        await using var blocker = fixture.NewContext();
        await using var tx = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(733050, hashtext({e.RepositoryId.ToString()}))");
        var pending = Task.Run(async () =>
        {
            await using var db = fixture.NewContext();
            return await Journal(db, e).ConfirmAsync(intent.Id,
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
        var refused = await Assert.ThrowsAsync<
            GitHubRemoteSandboxDispatchConfirmationException>(async () =>
                await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("SandboxDispatchConfirmationIntentStale", refused.Code);
        await using var check = fixture.NewContext();
        Assert.Empty(await check.GitHubRemoteSandboxDispatchConfirmations
            .AsNoTracking().Where(x => x.IntentId == intent.Id).ToArrayAsync());
    }
}
