using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// F-078 uses real PostgreSQL 17, the existing full F-075 authority
// fixture and a truly rehashable F-077 preflight history. No sender,
// installation bearer token, GitHub API mutation or remote push.
public sealed class PostgresGitHubRemoteSandboxReviewSessionTests(
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
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static GitHubRemoteTransmitHazardOptions Options(
        PostgresGitHubRemoteDispatchReviewTests.TestCase e) =>
        new(true, e.RepositoryId, "gh-org/sandbox");

    private static async Task<GitHubRemoteCreateOnlyDispatchReview>
        SeedAndReviewAsync(AppDbContext db,
            PostgresGitHubRemoteDispatchReviewTests.TestCase e,
            bool addPreflight = true)
    {
        await PostgresGitHubRemoteDispatchReviewTests.SeedAsync(db, e);
        if (addPreflight)
            await new PostgresGitHubRemoteAppPreflightEvidenceJournal(
                db, new(true, e.RepositoryId, "gh-org/sandbox"))
                .RecordAsync(e.Preflight, e.Preflight.ObservedAtUtc, default);
        return await e.Verify(db);
    }

    [Fact]
    public async Task F078_full_F075_and_F077_authority_records_one_nonexecutable_session()
    {
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        await using var db = fixture.NewContext();
        var review = await SeedAndReviewAsync(db, e);
        var journal = new PostgresGitHubRemoteSandboxReviewSessionJournal(
            db, Options(e));
        var receipt = await journal.RecordAsync(review, e.Actor, default);
        Assert.Equal(e.HazardId, receipt.HazardId);
        Assert.Equal(e.GrantId, receipt.GrantId);
        Assert.Equal(e.Preflight.EvidenceSha256, receipt.PreflightSha256);
        Assert.Equal(review.EvidenceSha256, receipt.DispatchReviewSha256);
        Assert.Equal(review.LastRefObservationSha256,
            receipt.LiveRefEvidenceSha256);
        Assert.Equal(GitHubRemoteSandboxReviewSessionIntegrity.Recorded,
            receipt.State);
        GitHubRemoteSandboxReviewSessionIntegrity.Verify(receipt);
        Assert.False(receipt.TransportInvoked);
        Assert.False(receipt.RemoteWritesAuthorized);
        Assert.False(receipt.MayAutomaticallyRetry);
        Assert.False(receipt.MigrationComplete);
        Assert.Equal(64, receipt.EvidenceSha256.Length);

        var view = await journal.InspectAsync(
            receipt.Id, e.RepositoryId, default);
        Assert.NotNull(view);
        Assert.Equal(GitHubRemoteSandboxReviewSessionIntegrity.Current,
            view.Status);
        Assert.False(view.HistoricalOnly);
        Assert.False(view.CanExecute);
        Assert.False(view.CanRetry);
        Assert.False(view.MigrationComplete);
        Assert.Equal(1, await db.GitHubRemoteSandboxReviewSessions
            .AsNoTracking().CountAsync(x => x.HazardId == e.HazardId));
        Assert.Equal(1, await db.GitHubRemoteSandboxReviewSessionEvents
            .AsNoTracking().CountAsync(x => x.SessionId == receipt.Id));
        Assert.Equal(GitHubRemoteTransmitHazardStates.PreparedWriteDisabled,
            (await db.GitHubRemoteTransmitHazards.AsNoTracking()
                .SingleAsync(x => x.Id == e.HazardId)).State);
    }

    [Fact]
    public async Task F078_six_replicas_append_exactly_one_session_and_audit_event()
    {
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        GitHubRemoteCreateOnlyDispatchReview review;
        await using (var setup = fixture.NewContext())
            review = await SeedAndReviewAsync(setup, e);

        var reviews = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(async _ =>
            {
                await using var replica = fixture.NewContext();
                var journal = new PostgresGitHubRemoteSandboxReviewSessionJournal(
                    replica, Options(e));
                return await journal.RecordAsync(review, e.Actor, default);
            }));
        Assert.All(reviews, x =>
        {
            Assert.Equal(reviews[0].Id, x.Id);
            Assert.Equal(reviews[0].EvidenceSha256, x.EvidenceSha256);
            Assert.False(x.RemoteWritesAuthorized);
            Assert.False(x.MayAutomaticallyRetry);
        });
        await using var check = fixture.NewContext();
        Assert.Single(await check.GitHubRemoteSandboxReviewSessions
            .AsNoTracking().Where(x => x.HazardId == e.HazardId)
            .ToArrayAsync());
        Assert.Single(await check.GitHubRemoteSandboxReviewSessionEvents
            .AsNoTracking().Where(x => x.SessionId == reviews[0].Id)
            .ToArrayAsync());
    }

    [Theory]
    [InlineData("alternate-actor")]
    [InlineData("alternate-review")]
    [InlineData("alternate-ref")]
    public async Task F078_duplicate_epoch_with_changed_review_evidence_is_refused(
        string mutation)
    {
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        await using var db = fixture.NewContext();
        var review = await SeedAndReviewAsync(db, e);
        var journal = new PostgresGitHubRemoteSandboxReviewSessionJournal(
            db, Options(e));
        await journal.RecordAsync(review, e.Actor, default);
        var changed = mutation switch
        {
            "alternate-review" => review with {
                EvidenceSha256 = new string('f', 64)
            },
            "alternate-ref" => review with {
                LastRefObservationSha256 = new string('f', 64)
            },
            _ => review
        };
        var actor = mutation == "alternate-actor"
            ? Guid.NewGuid() : e.Actor;
        var error = await Assert.ThrowsAsync<
            GitHubRemoteSandboxReviewSessionException>(
            () => journal.RecordAsync(changed, actor, default));
        Assert.Equal("SandboxReviewSessionConflict", error.Code);
        Assert.Equal(1, await db.GitHubRemoteSandboxReviewSessionEvents
            .AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task F078_hazard_may_have_transmitted_turns_session_into_historical_only()
    {
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        await using var db = fixture.NewContext();
        var review = await SeedAndReviewAsync(db, e);
        var journal = new PostgresGitHubRemoteSandboxReviewSessionJournal(
            db, Options(e));
        var receipt = await journal.RecordAsync(review, e.Actor, default);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE github_remote_transmit_hazards
            SET state='MayHaveTransmitted',
                marked_by={Guid.NewGuid()},
                updated_at_utc=clock_timestamp()
            WHERE id={e.HazardId}
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_remote_transmit_hazard_events (
                hazard_id,type,actor_id,occurred_at_utc)
            VALUES ({e.HazardId},{"MayHaveTransmitted"},
                {Guid.NewGuid()},clock_timestamp())
            """);

        var inspected = await journal.InspectAsync(
            receipt.Id, e.RepositoryId, default);
        Assert.NotNull(inspected);
        Assert.Equal(GitHubRemoteSandboxReviewSessionIntegrity.Historical,
            inspected.Status);
        Assert.True(inspected.HistoricalOnly);
        Assert.False(inspected.CanExecute);
        Assert.False(inspected.CanRetry);
        Assert.Equal(receipt.EvidenceSha256, inspected.Session.EvidenceSha256);
        var refused = await Assert.ThrowsAsync<
            GitHubRemoteSandboxReviewSessionException>(
            () => journal.RecordAsync(review, e.Actor, default));
        Assert.Equal("SandboxReviewSessionAuthorityChanged", refused.Code);
    }

    [Fact]
    public async Task F078_missing_F077_historical_preflight_prevents_session_record()
    {
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        await using var db = fixture.NewContext();
        var review = await SeedAndReviewAsync(
            db, e, addPreflight: false);
        var journal = new PostgresGitHubRemoteSandboxReviewSessionJournal(
            db, Options(e));
        var refused = await Assert.ThrowsAsync<
            GitHubRemoteSandboxReviewSessionException>(
            () => journal.RecordAsync(review, e.Actor, default));
        Assert.Equal("SandboxReviewSessionPreflightInvalid", refused.Code);
        Assert.Empty(await db.GitHubRemoteSandboxReviewSessions
            .AsNoTracking().Where(x => x.HazardId == e.HazardId)
            .ToArrayAsync());
    }

    [Fact]
    public async Task F078_postgresql_forbids_updating_or_deleting_session_and_audit()
    {
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        await using var db = fixture.NewContext();
        var review = await SeedAndReviewAsync(db, e);
        var journal = new PostgresGitHubRemoteSandboxReviewSessionJournal(
            db, Options(e));
        var receipt = await journal.RecordAsync(review, e.Actor, default);
        foreach (var sql in new[] {
            "UPDATE github_remote_sandbox_review_sessions SET remote_writes_authorized=true WHERE id=@id",
            "UPDATE github_remote_sandbox_review_sessions SET evidence_sha256='ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff' WHERE id=@id",
            "DELETE FROM github_remote_sandbox_review_sessions WHERE id=@id",
            "DELETE FROM github_remote_sandbox_review_session_events WHERE session_id=@id"
        })
        {
            await Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.ExecuteSqlRawAsync(sql,
                    new NpgsqlParameter("id", receipt.Id)));
        }
        Assert.NotNull(await journal.InspectAsync(
            receipt.Id, e.RepositoryId, default));
        Assert.Single(await db.GitHubRemoteSandboxReviewSessionEvents
            .AsNoTracking().Where(x => x.SessionId == receipt.Id)
            .ToArrayAsync());
    }

    [Fact]
    public async Task F078_a_competing_F055_mark_commits_before_waiting_session_is_recorded()
    {
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        GitHubRemoteCreateOnlyDispatchReview review;
        await using (var setup = fixture.NewContext())
            review = await SeedAndReviewAsync(setup, e);
        await using var blocker = fixture.NewContext();
        await using var tx = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(733050, hashtext({e.RepositoryId.ToString()}))");

        var pending = Task.Run(async () =>
        {
            await using var replica = fixture.NewContext();
            return await new PostgresGitHubRemoteSandboxReviewSessionJournal(
                replica, Options(e)).RecordAsync(review, e.Actor, default);
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
            GitHubRemoteSandboxReviewSessionException>(
            async () => await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("SandboxReviewSessionAuthorityChanged", error.Code);
        await using var check = fixture.NewContext();
        Assert.Empty(await check.GitHubRemoteSandboxReviewSessions
            .AsNoTracking().Where(x => x.HazardId == e.HazardId)
            .ToArrayAsync());
    }
}
