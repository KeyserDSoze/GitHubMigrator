using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Actual PostgreSQL 17, concurrent DbContexts, FK/unique/check constraints
// and durable events. No GitHub remote credentials or write operations.
public sealed class PostgresGitHubRemoteTestGrantTests(
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
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Six_replicas_propose_only_one_grant_per_fence_epoch()
    {
        var evidence = Evidence(DateTimeOffset.UtcNow);
        await using (var setup = fixture.NewContext())
            await SeedAsync(setup, evidence);

        async Task<(GitHubRemoteTestGrantClaim? Claim, string? Error)> ProposeAsync()
        {
            await using var db = fixture.NewContext();
            try
            {
                return (await Store(db, evidence).ProposeAsync(evidence, default),
                    null);
            }
            catch (GitHubRemoteTestGrantException ex)
            { return (null, ex.Code); }
        }

        var results = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => ProposeAsync()));
        var claim = Assert.Single(results.Where(x => x.Claim is not null)).Claim!;
        Assert.All(results.Where(x => x.Claim is null), x =>
            Assert.Equal("RemoteTestGrantEpochConsumed", x.Error));
        Assert.Equal(GitHubRemoteTestGrantStates.Proposed, claim.Status.State);
        Assert.NotEqual(Guid.Empty, claim.CancellationToken);

        await using var view = fixture.NewContext();
        var store = Store(view, evidence);
        var status = await store.GetAsync(claim.Status.Id,
            evidence.Fence.RepositoryId, default);
        Assert.NotNull(status);
        Assert.False(status.ExecutionAuthorized);
        Assert.False(status.RemoteWritesPerformed);
        Assert.False(status.ReadyForExecution);
        Assert.DoesNotContain(claim.CancellationToken.ToString("D"),
            System.Text.Json.JsonSerializer.Serialize(status));
        Assert.Equal(new[] { "Proposed" },
            (await store.EventsAsync(claim.Status.Id,
                evidence.Fence.RepositoryId, default)).Select(x => x.Type));

        var wrongActor = await Assert.ThrowsAsync<GitHubRemoteTestGrantException>(
            () => store.ReviewAsync(claim.Status.Id, evidence, default));
        Assert.Equal("RemoteTestGrantReviewStaleOrSameActor", wrongActor.Code);
        var unrelatedPreviousActor = await Assert.ThrowsAsync<GitHubRemoteTestGrantException>(
            () => store.ReviewAsync(claim.Status.Id,
                evidence with { ActorId = evidence.Attempt.RequestedBy }, default));
        Assert.Equal("RemoteTestGrantEvidenceInvalid", unrelatedPreviousActor.Code);

        var reviewer = evidence with { ActorId = Guid.NewGuid() };
        var reviewed = await store.ReviewAsync(claim.Status.Id, reviewer, default);
        Assert.Equal(GitHubRemoteTestGrantStates.ReviewedWriteDisabled,
            reviewed.State);
        Assert.Equal(reviewer.ActorId, reviewed.ReviewedBy);
        Assert.False(reviewed.ExecutionAuthorized);
        Assert.False(reviewed.RemoteWritesPerformed);
        Assert.False(reviewed.ReadyForExecution);

        Assert.False(await store.CancelAsync(claim.Status.Id,
            evidence.Fence.RepositoryId, reviewer.ActorId,
            claim.CancellationToken, default));
        Assert.False(await store.CancelAsync(claim.Status.Id,
            evidence.Fence.RepositoryId, evidence.ActorId,
            Guid.NewGuid(), default));
        Assert.True(await store.CancelAsync(claim.Status.Id,
            evidence.Fence.RepositoryId, evidence.ActorId,
            claim.CancellationToken, default));
        Assert.False(await store.CancelAsync(claim.Status.Id,
            evidence.Fence.RepositoryId, evidence.ActorId,
            claim.CancellationToken, default));
        Assert.Equal(new[] { "Proposed", "ReviewedWriteDisabled", "Cancelled" },
            (await store.EventsAsync(claim.Status.Id,
                evidence.Fence.RepositoryId, default)).Select(x => x.Type));

        var replay = await Assert.ThrowsAsync<GitHubRemoteTestGrantException>(
            () => store.ProposeAsync(evidence, default));
        Assert.Equal("RemoteTestGrantEpochConsumed", replay.Code);
    }

    [Fact]
    public async Task Expired_proposal_requires_manual_independent_no_write_recovery()
    {
        var start = DateTimeOffset.UtcNow;
        var evidence = Evidence(start);
        await using var db = fixture.NewContext();
        await SeedAsync(db, evidence);
        var clock = new AdjustableClock(start);
        var store = Store(db, evidence, clock);
        var claim = await store.ProposeAsync(evidence, default);

        clock.Advance(TimeSpan.FromSeconds(22));
        // Even if the quarantine worker was unavailable, a cancellation
        // request cannot reset a stale fence or extend the deadline.
        Assert.False(await store.CancelAsync(claim.Status.Id,
            evidence.Fence.RepositoryId, evidence.ActorId,
            claim.CancellationToken, default));
        Assert.Equal(GitHubRemoteTestGrantStates.RecoveryRequired,
            (await store.GetAsync(claim.Status.Id,
                evidence.Fence.RepositoryId, default))!.State);
        Assert.False(await store.QuarantineNextExpiredAsync(default));
        Assert.Equal(1, (await store.EventsAsync(claim.Status.Id,
            evidence.Fence.RepositoryId, default))
            .Count(x => x.Type == "RecoveryRequired"));

        var badAttestation = await Assert.ThrowsAsync<GitHubRemoteTestGrantException>(
            () => store.RecoverAsync(claim.Status.Id, evidence.Fence.RepositoryId,
                Guid.NewGuid(), "I promise", default));
        Assert.Equal("RemoteTestGrantRecoveryAttestationRequired",
            badAttestation.Code);
        var wrongActor = await Assert.ThrowsAsync<GitHubRemoteTestGrantException>(
            () => store.RecoverAsync(claim.Status.Id, evidence.Fence.RepositoryId,
                evidence.ActorId, GitHubRemoteTestGrantIntegrity.RecoveryText,
                default));
        Assert.Equal("RemoteTestGrantRecoveryMustBeIndependent",
            wrongActor.Code);
        Assert.True(await store.RecoverAsync(claim.Status.Id,
            evidence.Fence.RepositoryId, Guid.NewGuid(),
            GitHubRemoteTestGrantIntegrity.RecoveryText, default));
        Assert.False(await store.RecoverAsync(claim.Status.Id,
            evidence.Fence.RepositoryId, Guid.NewGuid(),
            GitHubRemoteTestGrantIntegrity.RecoveryText, default));
        Assert.Equal(new[] { "Proposed", "RecoveryRequired", "RecoveredNoWrites" },
            (await store.EventsAsync(claim.Status.Id,
                evidence.Fence.RepositoryId, default)).Select(x => x.Type));
    }

    [Fact]
    public async Task Two_human_review_does_not_circumvent_Postgres_no_write_flags()
    {
        var evidence = Evidence(DateTimeOffset.UtcNow);
        await using var db = fixture.NewContext();
        await SeedAsync(db, evidence);
        var store = Store(db, evidence);
        var claim = await store.ProposeAsync(evidence, default);
        await store.ReviewAsync(claim.Status.Id,
            evidence with { ActorId = Guid.NewGuid() }, default);

        Func<Task>[] forbidden = [
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE github_remote_test_grants SET execution_authorized={true} WHERE id={claim.Status.Id}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE github_remote_test_grants SET remote_writes_performed={true} WHERE id={claim.Status.Id}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE github_remote_test_grants SET ready_for_execution={true} WHERE id={claim.Status.Id}")
        ];
        foreach (var write in forbidden)
            await Assert.ThrowsAnyAsync<Exception>(write);
        var status = await store.GetAsync(
            claim.Status.Id, evidence.Fence.RepositoryId, default);
        Assert.False(status!.ExecutionAuthorized);
        Assert.False(status.RemoteWritesPerformed);
        Assert.False(status.ReadyForExecution);
        Assert.True(await store.CancelAsync(claim.Status.Id,
            evidence.Fence.RepositoryId, evidence.ActorId,
            claim.CancellationToken, default));
    }

    [Fact]
    public async Task Fake_policy_wrong_numeric_repo_and_changed_F050_are_refused()
    {
        var evidence = Evidence(DateTimeOffset.UtcNow);
        await using var db = fixture.NewContext();
        await SeedAsync(db, evidence);
        var store = Store(db, evidence);
        var altered = evidence with { Preflight = evidence.Preflight with {
            OriginalPolicySha256 = Sha('f')
        }};
        var ex = await Assert.ThrowsAsync<GitHubRemoteTestGrantException>(
            () => store.ProposeAsync(altered, default));
        Assert.Equal("RemoteTestGrantEvidenceInvalid", ex.Code);

        ex = await Assert.ThrowsAsync<GitHubRemoteTestGrantException>(
            () => new PostgresGitHubRemoteTestGrantStore(db,
                TimeProvider.System, new(true,
                    evidence.Fence.RepositoryId + 1, "gh-org/test-sandbox"))
                .ProposeAsync(evidence, default));
        Assert.Equal("RemoteTestGrantEvidenceInvalid", ex.Code);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE github_remote_sandbox_fences
            SET state='Released'
            WHERE repository_id={evidence.Fence.RepositoryId}
            """);
        ex = await Assert.ThrowsAsync<GitHubRemoteTestGrantException>(
            () => store.ProposeAsync(evidence, default));
        Assert.Equal("RemoteTestGrantStoredBarrierChanged", ex.Code);
        Assert.Null(await db.GitHubRemoteTestGrants.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RepositoryId ==
                evidence.Fence.RepositoryId));
    }

    private static PostgresGitHubRemoteTestGrantStore Store(
        AppDbContext db, GitHubRemoteTestGrantEvidence e,
        TimeProvider? clock = null) =>
        new(db, clock ?? TimeProvider.System, new(
            true, e.Fence.RepositoryId,
            e.Fence.Organization + "/" + e.Fence.Repository));

    private static string Sha(char c) => new(c, 64);

    private static GitHubRemoteTestGrantEvidence Evidence(DateTimeOffset now)
    {
        var repoId = Random.Shared.NextInt64(
            100_000_000_000L, 900_000_000_000L);
        var preparation = Guid.NewGuid();
        var plan = Guid.NewGuid();
        var scope = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var fenceId = Guid.NewGuid();
        var packet = Sha('a');
        var candidate = Sha('b');
        var policy = Sha('c');
        var requestedBy = Guid.NewGuid();
        var reviewedBy = Guid.NewGuid();
        var f050Proposer = Guid.NewGuid();
        var f050Reviewer = Guid.NewGuid();
        var attempt = new GitHubTransferAttemptView(
            attemptId, preparation, plan, scope, repoId,
            "gh-org", "test-sandbox", packet, policy, candidate,
            1, 1, GitHubTransferAttemptStates.BlockedAtWriteBarrier,
            requestedBy, reviewedBy, now, now, now, null,
            false, false, false, false);
        var fence = new GitHubRemoteSandboxFenceStatus(
            fenceId, repoId, "gh-org", "test-sandbox",
            preparation, plan, scope, attemptId,
            packet, candidate, policy, 1,
            GitHubRemoteSandboxFenceStates.ReviewedWriteDisabled,
            f050Proposer, f050Reviewer,
            now.AddSeconds(20), now, false, false, false);
        var receipt = new GitHubRemoteAppPreflightReceipt(
            fenceId, 1, repoId, packet, candidate, policy, Sha('d'),
            Sha('e'), Sha('e'), 2, 1,
            now, now.AddMinutes(45), Sha('f'),
            GitHubRemoteAppPreflightIntegrity.VerifiedState,
            false, false, false);
        return new(receipt, fence, attempt, Guid.NewGuid());
    }

    private static async Task SeedAsync(
        AppDbContext db, GitHubRemoteTestGrantEvidence e)
    {
        var f = e.Fence;
        var a = e.Attempt;
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_transfer_attempts (
                id, preparation_id, plan_id, scope_id, repository_id,
                organization, repository, packet_sha256, policy_sha256,
                candidate_set_sha256, candidate_count, policy_observed_at_utc,
                lease_token, lease_generation, requested_by, reviewed_by,
                created_at_utc, reviewed_at_utc, blocked_at_utc, expired_at_utc,
                state, transport_attempted, execution_authorized,
                remote_writes_performed, ready_for_execution)
            VALUES ({a.Id},{a.PreparationId},{a.PlanId},{a.ScopeId},
                {a.RepositoryId},{a.Organization},{a.Repository},
                {a.PacketSha256},{a.PolicySha256},{a.CandidateSetSha256},
                {a.CandidateCount},{now},{Guid.NewGuid()},{1L},
                {a.RequestedBy},{a.ReviewedBy},{now},{now},{now},NULL,
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
            VALUES ({f.RepositoryId},{f.RequestId},{f.PreparationId},
                {f.PlanId},{f.ScopeId},{f.AttemptId},
                {f.Organization},{f.Repository},
                {f.PacketSha256},{f.CandidateSetSha256},{f.PolicySha256},
                {Sha('1')},{f.Generation},'ReviewedWriteDisabled',
                {f.RequestedBy},{f.ReviewedBy},{f.DeadlineUtc},{now},
                false,false,false)
            """);
    }

    private sealed class AdjustableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan span) => _now = _now.Add(span);
    }
}
