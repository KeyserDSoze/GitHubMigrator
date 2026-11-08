using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Full F-075/F-077/F-078/F-080/F-081 -> F-082 PostgreSQL chain.
// F-082 does NOT contact GitHub or invoke a Git push, even in tests.
public sealed class PostgresGitHubRemoteSandboxPreSendBarrierTests(
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
        await GitHubRemoteSandboxPreSendBarrierSchema.InitializeAsync(services);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static GitHubRemoteTransmitHazardOptions Options(
        PostgresGitHubRemoteDispatchReviewTests.TestCase e) =>
        new(true, e.RepositoryId, "gh-org/sandbox");

    private static PostgresGitHubRemoteSandboxReviewSessionJournal Sessions(
        AppDbContext db, PostgresGitHubRemoteDispatchReviewTests.TestCase e) =>
        new(db, Options(e));

    private static PostgresGitHubRemoteSandboxDispatchIntentJournal Intents(
        AppDbContext db, PostgresGitHubRemoteDispatchReviewTests.TestCase e) =>
        new(db, Options(e), Sessions(db, e));

    private static PostgresGitHubRemoteSandboxDispatchConfirmationJournal Confirmations(
        AppDbContext db, PostgresGitHubRemoteDispatchReviewTests.TestCase e) =>
        new(db, Options(e), Intents(db, e), Sessions(db, e));

    private static PostgresGitHubRemoteSandboxPreSendBarrierJournal Journal(
        AppDbContext db, PostgresGitHubRemoteDispatchReviewTests.TestCase e) =>
        new(db, Options(e), Confirmations(db, e));

    private static async Task<GitHubRemoteSandboxDispatchConfirmationReceipt>
        SeedAsync(AppDbContext db, PostgresGitHubRemoteDispatchReviewTests.TestCase e)
    {
        await PostgresGitHubRemoteDispatchReviewTests.SeedAsync(db, e);
        await new PostgresGitHubRemoteAppPreflightEvidenceJournal(
            db, new(true, e.RepositoryId, "gh-org/sandbox"))
            .RecordAsync(e.Preflight, e.Preflight.ObservedAtUtc, default);
        var review = await e.Verify(db);
        var session = await Sessions(db, e).RecordAsync(review, e.Actor, default);
        var p = e.Source.Packet;
        var intent = await Intents(db, e).RecordAsync(session.Id,
            p.PreparationId, p.PlanId, p.ScopeId, Guid.NewGuid(), default);
        return await Confirmations(db, e).ConfirmAsync(
            intent.Id, p.PreparationId, p.PlanId, p.ScopeId,
            Guid.NewGuid(), default);
    }

    [Fact]
    public async Task F082_atomic_possible_send_marks_F055_and_one_hash_sealed_F082_record()
    {
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        await using var db = fixture.NewContext();
        var confirmation = await SeedAsync(db, e);
        var p = e.Source.Packet;
        var actor = Guid.NewGuid();
        var journal = Journal(db, e);
        var marker = await journal.RecordPossibleSendAsync(
            confirmation.Id, confirmation.IntentId,
            p.PreparationId, p.PlanId, p.ScopeId, actor, default);

        GitHubRemoteSandboxPreSendBarrierIntegrity.Verify(marker);
        Assert.Equal(confirmation.EvidenceSha256,
            marker.ConfirmationEvidenceSha256);
        Assert.Equal(confirmation.IntentEvidenceSha256,
            marker.IntentEvidenceSha256);
        Assert.Equal(confirmation.SessionEvidenceSha256,
            marker.SessionEvidenceSha256);
        Assert.Equal("MayHaveTransmittedWriteDisabled", marker.State);
        Assert.False(marker.CanExecute);
        Assert.False(marker.TransportInvoked);
        Assert.False(marker.MayAutomaticallyRetry);
        Assert.False(marker.MigrationComplete);

        var hazard = await db.GitHubRemoteTransmitHazards.AsNoTracking()
            .SingleAsync(x => x.Id == confirmation.HazardId);
        Assert.Equal(GitHubRemoteTransmitHazardStates.MayHaveTransmitted,
            hazard.State);
        Assert.Equal(actor, hazard.MarkedBy);
        Assert.False(hazard.TransportAuthorized);
        Assert.False(hazard.TransportInvoked);
        Assert.False(hazard.RemoteWritesPerformed);
        Assert.False(hazard.MayAutomaticallyRetry);
        Assert.Equal(marker.RecordedAtUtc, hazard.UpdatedAtUtc);

        var inspection = await journal.InspectScopedAsync(marker.Id,
            confirmation.Id, confirmation.IntentId,
            p.PreparationId, p.PlanId, p.ScopeId, default);
        Assert.NotNull(inspection);
        Assert.Equal("HistoricalOnly", inspection.Status);
        Assert.False(inspection.CanExecute);
        Assert.False(inspection.CanRetry);
        Assert.False(inspection.MigrationComplete);
        Assert.Equal(marker.EvidenceSha256,
            inspection.Barrier.EvidenceSha256);
        Assert.Single(await db.GitHubRemoteSandboxPreSendBarriers.AsNoTracking()
            .Where(x => x.HazardId == confirmation.HazardId).ToArrayAsync());
        Assert.Single(await db.GitHubRemoteSandboxPreSendBarrierEvents.AsNoTracking()
            .Where(x => x.BarrierId == marker.Id).ToArrayAsync());
        Assert.Equal(new[] {
            GitHubRemoteTransmitHazardStates.PreparedWriteDisabled,
            GitHubRemoteTransmitHazardStates.MayHaveTransmitted
        }, (await db.GitHubRemoteTransmitHazardEvents.AsNoTracking()
            .Where(x => x.HazardId == confirmation.HazardId)
            .OrderBy(x => x.Id).Select(x => x.Type).ToArrayAsync()));

        var replay = await Assert.ThrowsAsync<
            GitHubRemoteSandboxPreSendBarrierException>(() =>
                journal.RecordPossibleSendAsync(confirmation.Id,
                    confirmation.IntentId, p.PreparationId, p.PlanId,
                    p.ScopeId, actor, default));
        Assert.Equal("PreSendBarrierConfirmationStale", replay.Code);
    }

    [Fact]
    public async Task F082_six_replicas_consume_only_one_pre_send_epoch()
    {
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        GitHubRemoteSandboxDispatchConfirmationReceipt confirmation;
        await using (var seed = fixture.NewContext())
            confirmation = await SeedAsync(seed, e);
        var p = e.Source.Packet;
        var actor = Guid.NewGuid();
        async Task<(GitHubRemoteSandboxPreSendBarrierReceipt? Value, string? Refused)> Try()
        {
            await using var db = fixture.NewContext();
            try
            {
                return (await Journal(db, e).RecordPossibleSendAsync(
                    confirmation.Id, confirmation.IntentId,
                    p.PreparationId, p.PlanId, p.ScopeId, actor, default), null);
            }
            catch (GitHubRemoteSandboxPreSendBarrierException ex)
            { return (null, ex.Code); }
        }
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => Try()));
        Assert.Single(outcomes.Where(x => x.Value is not null));
        Assert.All(outcomes.Where(x => x.Value is null), x =>
            Assert.Equal("PreSendBarrierConfirmationStale", x.Refused));
        await using var check = fixture.NewContext();
        Assert.Single(await check.GitHubRemoteSandboxPreSendBarriers.AsNoTracking()
            .Where(x => x.ConfirmationId == confirmation.Id).ToArrayAsync());
        var winnerId = outcomes.Single(x => x.Value is not null).Value!.Id;
        Assert.Single(await check.GitHubRemoteSandboxPreSendBarrierEvents.AsNoTracking()
            .Where(x => x.BarrierId == winnerId).ToArrayAsync());
        Assert.Single(await check.GitHubRemoteTransmitHazardEvents.AsNoTracking()
            .Where(x => x.HazardId == confirmation.HazardId &&
                x.Type == GitHubRemoteTransmitHazardStates.MayHaveTransmitted)
            .ToArrayAsync());
    }

    [Fact]
    public async Task F082_cross_scope_or_prior_marker_cannot_consume_confirmation()
    {
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        await using var db = fixture.NewContext();
        var confirmation = await SeedAsync(db, e);
        var p = e.Source.Packet;
        var journal = Journal(db, e);
        var actor = Guid.NewGuid();

        var notFound = await Assert.ThrowsAsync<
            GitHubRemoteSandboxPreSendBarrierException>(() =>
                journal.RecordPossibleSendAsync(confirmation.Id,
                    confirmation.IntentId, p.PreparationId,
                    Guid.NewGuid(), p.ScopeId, actor, default));
        Assert.Equal("PreSendBarrierConfirmationNotFound", notFound.Code);
        var sameActor = await Assert.ThrowsAsync<
            GitHubRemoteSandboxPreSendBarrierException>(() =>
                journal.RecordPossibleSendAsync(confirmation.Id,
                    confirmation.IntentId, p.PreparationId,
                    p.PlanId, p.ScopeId, confirmation.ConfirmedBy, default));
        Assert.Equal("PreSendBarrierConfirmationStale", sameActor.Code);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE github_remote_transmit_hazards
            SET state='MayHaveTransmitted',marked_by={Guid.NewGuid()},
                updated_at_utc=clock_timestamp()
            WHERE id={confirmation.HazardId}
            """);
        var stale = await Assert.ThrowsAsync<
            GitHubRemoteSandboxPreSendBarrierException>(() =>
                journal.RecordPossibleSendAsync(confirmation.Id,
                    confirmation.IntentId, p.PreparationId,
                    p.PlanId, p.ScopeId, actor, default));
        Assert.Equal("PreSendBarrierConfirmationStale", stale.Code);
        Assert.Empty(await db.GitHubRemoteSandboxPreSendBarriers.AsNoTracking()
            .Where(x => x.HazardId == confirmation.HazardId).ToArrayAsync());
    }

    [Fact]
    public async Task F082_competing_F055_mark_wins_lock_before_F082_and_prevents_double_send()
    {
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        GitHubRemoteSandboxDispatchConfirmationReceipt confirmation;
        await using (var seed = fixture.NewContext())
            confirmation = await SeedAsync(seed, e);
        var p = e.Source.Packet;
        await using var blocker = fixture.NewContext();
        await using var tx = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(733050, hashtext({e.RepositoryId.ToString()}))");
        var pending = Task.Run(async () =>
        {
            await using var db = fixture.NewContext();
            return await Journal(db, e).RecordPossibleSendAsync(
                confirmation.Id, confirmation.IntentId,
                p.PreparationId, p.PlanId, p.ScopeId,
                Guid.NewGuid(), default);
        });
        await Task.Delay(140);
        Assert.False(pending.IsCompleted);
        await blocker.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE github_remote_transmit_hazards
            SET state='MayHaveTransmitted',marked_by={Guid.NewGuid()},
                updated_at_utc=clock_timestamp()
            WHERE id={confirmation.HazardId}
            """);
        await tx.CommitAsync();
        var error = await Assert.ThrowsAsync<
            GitHubRemoteSandboxPreSendBarrierException>(async () =>
                await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("PreSendBarrierConfirmationStale", error.Code);
        await using var check = fixture.NewContext();
        Assert.Empty(await check.GitHubRemoteSandboxPreSendBarriers.AsNoTracking()
            .Where(x => x.HazardId == confirmation.HazardId).ToArrayAsync());
    }

    [Fact]
    public async Task F082_SQL_refuses_mutation_of_pre_send_and_audit_evidence()
    {
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        await using var db = fixture.NewContext();
        var confirmation = await SeedAsync(db, e);
        var p = e.Source.Packet;
        var journal = Journal(db, e);
        var marker = await journal.RecordPossibleSendAsync(
            confirmation.Id, confirmation.IntentId,
            p.PreparationId, p.PlanId, p.ScopeId, Guid.NewGuid(), default);
        foreach (var sql in new[] {
            "UPDATE github_remote_sandbox_pre_send_barriers SET can_execute=true WHERE id=@id",
            "UPDATE github_remote_sandbox_pre_send_barriers SET evidence_sha256='ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff' WHERE id=@id",
            "DELETE FROM github_remote_sandbox_pre_send_barriers WHERE id=@id",
            "DELETE FROM github_remote_sandbox_pre_send_barrier_events WHERE barrier_id=@id"
        })
            await Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.ExecuteSqlRawAsync(sql,
                    new NpgsqlParameter("id", marker.Id)));
        var inspected = await journal.InspectScopedAsync(marker.Id,
            confirmation.Id, confirmation.IntentId,
            p.PreparationId, p.PlanId, p.ScopeId, default);
        Assert.NotNull(inspected);
        Assert.Equal(marker.EvidenceSha256, inspected.Barrier.EvidenceSha256);
        Assert.Null(await journal.InspectScopedAsync(marker.Id,
            confirmation.Id, confirmation.IntentId,
            Guid.NewGuid(), p.PlanId, p.ScopeId, default));
        Assert.Null(await journal.InspectScopedAsync(marker.Id,
            Guid.NewGuid(), confirmation.IntentId,
            p.PreparationId, p.PlanId, p.ScopeId, default));
    }

    [Fact]
    public async Task F082_disabled_by_default_refuses_without_touching_postgresql()
    {
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        await using var db = fixture.NewContext();
        var disabled = new PostgresGitHubRemoteSandboxPreSendBarrierJournal(
            db, new(false, e.RepositoryId, "gh-org/sandbox"),
            Confirmations(db, e));
        var error = await Assert.ThrowsAsync<
            GitHubRemoteSandboxPreSendBarrierException>(() =>
                disabled.RecordPossibleSendAsync(Guid.NewGuid(), Guid.NewGuid(),
                    Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                    Guid.NewGuid(), default));
        Assert.Equal("PreSendBarrierDisabled", error.Code);
    }
}
