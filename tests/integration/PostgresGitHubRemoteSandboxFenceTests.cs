using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

public sealed class PostgresGitHubRemoteSandboxFenceTests(
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
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Concurrent_replicas_propose_exactly_one_global_repository_fence()
    {
        var input = Input();
        await using (var setup = fixture.NewContext())
            await SeedAttemptAsync(setup, input.Attempt);

        async Task<(GitHubRemoteSandboxFenceClaim? Claim, string? Error)> ProposeAsync()
        {
            await using var db = fixture.NewContext();
            try
            {
                return (await Store(db, input).ProposeAsync(input, default), null);
            }
            catch (GitHubRemoteSandboxFenceException ex)
            { return (null, ex.Code); }
        }

        var results = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => ProposeAsync()));
        var winner = Assert.Single(results.Where(x => x.Claim is not null)).Claim!;
        Assert.All(results.Where(x => x.Error is not null),
            x => Assert.Equal("RemoteFenceAlreadyHeldOrQuarantined", x.Error));
        Assert.Equal(GitHubRemoteSandboxFenceStates.Proposed, winner.State);
        Assert.Equal(1, winner.Generation);
        Assert.False(winner.ExecutionAuthorized);
        Assert.False(winner.RemoteWritesPerformed);
        Assert.False(winner.ReadyForExecution);

        await using var verification = fixture.NewContext();
        var store = Store(verification, input);
        var status = await store.GetAsync(input.Packet.RepositoryId, default);
        Assert.NotNull(status);
        Assert.Equal(winner.RequestId, status.RequestId);
        Assert.DoesNotContain("FenceToken",
            System.Text.Json.JsonSerializer.Serialize(status));
        Assert.DoesNotContain(winner.FenceToken.ToString("D"),
            System.Text.Json.JsonSerializer.Serialize(status));
        Assert.Single((await store.EventsAsync(input.Packet.RepositoryId, default))
            .Where(x => x.Type == GitHubRemoteSandboxFenceStates.Proposed));

        var reviewer = input with { ActorId = Guid.NewGuid() };
        var approved = await store.ReviewAsync(reviewer, winner.RequestId,
            winner.Generation, default);
        Assert.Equal(GitHubRemoteSandboxFenceStates.ReviewedWriteDisabled,
            approved.State);
        Assert.Equal(reviewer.ActorId, approved.ReviewedBy);
        Assert.False(approved.ExecutionAuthorized);
        Assert.False(approved.RemoteWritesPerformed);
        Assert.False(approved.ReadyForExecution);

        var selfReview = await Assert.ThrowsAsync<GitHubRemoteSandboxFenceException>(
            () => store.ReviewAsync(input, winner.RequestId,
                winner.Generation, default));
        Assert.Equal("RemoteFenceReviewStaleOrSameActor", selfReview.Code);
        Assert.False(await store.ReleaseAsync(input.Packet.RepositoryId,
            winner.RequestId, Guid.NewGuid(), winner.Generation,
            input.ActorId, default));
        Assert.False(await store.ReleaseAsync(input.Packet.RepositoryId,
            winner.RequestId, winner.FenceToken, winner.Generation,
            reviewer.ActorId, default));
        Assert.True(await store.ReleaseAsync(input.Packet.RepositoryId,
            winner.RequestId, winner.FenceToken, winner.Generation,
            input.ActorId, default));
        Assert.False(await store.ReleaseAsync(input.Packet.RepositoryId,
            winner.RequestId, winner.FenceToken, winner.Generation,
            input.ActorId, default));
        Assert.Equal(new[] { "Proposed", "ReviewedWriteDisabled", "Released" },
            (await store.EventsAsync(input.Packet.RepositoryId, default))
                .Select(x => x.Type));
    }

    [Fact]
    public async Task Stale_proposal_is_quarantined_and_not_auto_reacquired()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new Clock(now);
        var input = Input();
        await using var db = fixture.NewContext();
        await SeedAttemptAsync(db, input.Attempt);
        var store = Store(db, input, clock);
        var first = await store.ProposeAsync(input, default);
        clock.Advance(TimeSpan.FromSeconds(26));

        var rejected = await Assert.ThrowsAsync<GitHubRemoteSandboxFenceException>(
            () => store.ProposeAsync(input, default));
        Assert.Equal("RemoteFenceAlreadyHeldOrQuarantined", rejected.Code);
        Assert.Equal(GitHubRemoteSandboxFenceStates.RecoveryRequired,
            (await store.GetAsync(input.Packet.RepositoryId, default))!.State);
        Assert.False(await store.QuarantineNextExpiredAsync(default));
        Assert.Equal(1, (await store.EventsAsync(input.Packet.RepositoryId, default))
            .Count(x => x.Type == "RecoveryRequired"));

        var wrongText = await Assert.ThrowsAsync<GitHubRemoteSandboxFenceException>(
            () => store.RecoverAsync(input with { ActorId = Guid.NewGuid() },
                first.RequestId, first.Generation, "YES", default));
        Assert.Equal("RemoteFenceRecoveryAttestationRequired", wrongText.Code);
        var samePerson = await Assert.ThrowsAsync<GitHubRemoteSandboxFenceException>(
            () => store.RecoverAsync(input, first.RequestId, first.Generation,
                GitHubRemoteSandboxFenceIntegrity.RecoveryText, default));
        Assert.Equal("RemoteFenceRecoveryMustBeIndependent", samePerson.Code);
        var operator2 = input with { ActorId = Guid.NewGuid() };
        Assert.True(await store.RecoverAsync(operator2, first.RequestId,
            first.Generation, GitHubRemoteSandboxFenceIntegrity.RecoveryText, default));
        Assert.Equal(GitHubRemoteSandboxFenceStates.RecoveredNoWrites,
            (await store.GetAsync(input.Packet.RepositoryId, default))!.State);

        var second = await store.ProposeAsync(input, default);
        Assert.Equal(2, second.Generation);
        Assert.NotEqual(first.RequestId, second.RequestId);
        Assert.NotEqual(first.FenceToken, second.FenceToken);
        Assert.False(await store.ReleaseAsync(input.Packet.RepositoryId,
            first.RequestId, first.FenceToken, first.Generation,
            input.ActorId, default));
        Assert.True(await store.ReleaseAsync(input.Packet.RepositoryId,
            second.RequestId, second.FenceToken, second.Generation,
            input.ActorId, default));
    }

    [Fact]
    public async Task Expired_review_commits_quarantine_even_when_it_throws()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var input = Input();
        await using var db = fixture.NewContext();
        await SeedAttemptAsync(db, input.Attempt);
        var store = Store(db, input, clock);
        var proposal = await store.ProposeAsync(input, default);
        clock.Advance(TimeSpan.FromSeconds(27));
        var failed = await Assert.ThrowsAsync<GitHubRemoteSandboxFenceException>(
            () => store.ReviewAsync(input with { ActorId = Guid.NewGuid() },
                proposal.RequestId, proposal.Generation, default));
        Assert.Equal("RemoteFenceReviewStaleOrSameActor", failed.Code);
        Assert.Equal(GitHubRemoteSandboxFenceStates.RecoveryRequired,
            (await store.GetAsync(input.Packet.RepositoryId, default))!.State);
        Assert.Equal(new[] { "Proposed", "RecoveryRequired" },
            (await store.EventsAsync(input.Packet.RepositoryId, default))
                .Select(x => x.Type));
    }

    [Fact]
    public async Task Reviewed_deadline_quarantines_and_requires_an_independent_recovery_actor()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var input = Input();
        await using var db = fixture.NewContext();
        await SeedAttemptAsync(db, input.Attempt);
        var store = Store(db, input, clock);
        var proposal = await store.ProposeAsync(input, default);
        var reviewer = input with { ActorId = Guid.NewGuid() };
        await store.ReviewAsync(reviewer, proposal.RequestId,
            proposal.Generation, default);
        clock.Advance(TimeSpan.FromSeconds(21));
        Assert.True(await store.QuarantineNextExpiredAsync(default));
        Assert.False(await store.QuarantineNextExpiredAsync(default));
        Assert.False(await store.ReleaseAsync(input.Packet.RepositoryId,
            proposal.RequestId, proposal.FenceToken, proposal.Generation,
            input.ActorId, default));
        await Assert.ThrowsAsync<GitHubRemoteSandboxFenceException>(
            () => store.RecoverAsync(reviewer, proposal.RequestId,
                proposal.Generation,
                GitHubRemoteSandboxFenceIntegrity.RecoveryText, default));
        var thirdActor = input with { ActorId = Guid.NewGuid() };
        Assert.True(await store.RecoverAsync(thirdActor, proposal.RequestId,
            proposal.Generation, GitHubRemoteSandboxFenceIntegrity.RecoveryText,
            default));
        Assert.Equal(new[] {
            "Proposed", "ReviewedWriteDisabled", "RecoveryRequired",
            "RecoveredNoWrites"
        }, (await store.EventsAsync(input.Packet.RepositoryId, default))
            .Select(x => x.Type));
    }

    [Fact]
    public async Task Sql_hard_denies_all_remote_transport_authorization_flags()
    {
        var input = Input();
        await using var db = fixture.NewContext();
        await SeedAttemptAsync(db, input.Attempt);
        var store = Store(db, input);
        var proposal = await store.ProposeAsync(input, default);
        Func<Task>[] attempts = [
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE github_remote_sandbox_fences SET execution_authorized = {true} WHERE repository_id = {input.Packet.RepositoryId}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE github_remote_sandbox_fences SET remote_writes_performed = {true} WHERE repository_id = {input.Packet.RepositoryId}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE github_remote_sandbox_fences SET ready_for_execution = {true} WHERE repository_id = {input.Packet.RepositoryId}")
        ];
        foreach (var badUpdate in attempts)
            await Assert.ThrowsAnyAsync<Exception>(badUpdate);
        var persisted = await store.GetAsync(input.Packet.RepositoryId, default);
        Assert.False(persisted!.ExecutionAuthorized);
        Assert.False(persisted.RemoteWritesPerformed);
        Assert.False(persisted.ReadyForExecution);
        Assert.True(await store.ReleaseAsync(input.Packet.RepositoryId,
            proposal.RequestId, proposal.FenceToken, proposal.Generation,
            input.ActorId, default));
    }

    [Fact]
    public async Task Scope_allowlist_mismatch_never_persists_a_proposal()
    {
        var input = Input();
        await using var db = fixture.NewContext();
        await SeedAttemptAsync(db, input.Attempt);
        var options = new GitHubRemoteSandboxFenceOptions(true,
            input.Packet.RepositoryId + 1, "gh-org/sandbox");
        var store = new PostgresGitHubRemoteSandboxFenceStore(
            db, TimeProvider.System, options);
        var blocked = await Assert.ThrowsAsync<GitHubRemoteSandboxFenceException>(
            () => store.ProposeAsync(input, default));
        Assert.Equal("RemoteFenceEvidenceInvalid", blocked.Code);
        Assert.Null(await db.GitHubRemoteSandboxFences.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RepositoryId == input.Packet.RepositoryId));
    }

    private PostgresGitHubRemoteSandboxFenceStore Store(
        AppDbContext db, GitHubRemoteSandboxFenceInput input,
        TimeProvider? time = null) => new(db, time ?? TimeProvider.System,
            new GitHubRemoteSandboxFenceOptions(
                true, input.Packet.RepositoryId,
                input.Packet.Organization + "/" + input.Packet.Repository));

    private static GitHubRemoteSandboxFenceInput Input()
    {
        var packet = new GitHubCreateOnlyPacket(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Random.Shared.NextInt64(100_000_000_000, 900_000_000_000),
            "gh-org", "sandbox", Sha('1'), Sha('2'), Sha('3'),
            Sha('4'), Sha('5'), Sha('6'), Sha('7'), Sha('8'),
            Sha('9'), Sha('a'), Sha('b'), 1, 0, 0,
            GitHubCreateOnlyPacketIntegrity.State,
            false, false, false, false);
        var requestedBy = Guid.NewGuid();
        var reviewedBy = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var attempt = new GitHubTransferAttemptView(
            Guid.NewGuid(), packet.PreparationId, packet.PlanId,
            packet.ScopeId, packet.RepositoryId, packet.Organization,
            packet.Repository, packet.EvidenceSha256, Sha('c'),
            packet.CandidateSetSha256, 1, 1,
            GitHubTransferAttemptStates.BlockedAtWriteBarrier,
            requestedBy, reviewedBy,
            now, now, now, null, false, false, false, false);
        return new(packet, attempt, Guid.NewGuid());
    }

    private static string Sha(char digit) => new(digit, 64);

    private static async Task SeedAttemptAsync(
        AppDbContext db, GitHubTransferAttemptView attempt)
    {
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_transfer_attempts (
                id, preparation_id, plan_id, scope_id, repository_id,
                organization, repository, packet_sha256, policy_sha256,
                candidate_set_sha256, candidate_count, policy_observed_at_utc,
                lease_token, lease_generation, requested_by, reviewed_by,
                created_at_utc, reviewed_at_utc, blocked_at_utc, expired_at_utc,
                state, transport_attempted, execution_authorized,
                remote_writes_performed, ready_for_execution
            ) VALUES (
                {attempt.Id}, {attempt.PreparationId}, {attempt.PlanId},
                {attempt.ScopeId}, {attempt.RepositoryId},
                {attempt.Organization}, {attempt.Repository},
                {attempt.PacketSha256}, {attempt.PolicySha256},
                {attempt.CandidateSetSha256}, {attempt.CandidateCount}, {now},
                {Guid.NewGuid()}, {attempt.LeaseGeneration},
                {attempt.RequestedBy}, {attempt.ReviewedBy},
                {now}, {now}, {now}, NULL, 'BlockedAtWriteBarrier',
                false, false, false, false)
            """);
    }

    private sealed class Clock(DateTimeOffset value) : TimeProvider
    {
        private DateTimeOffset _now = value;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan period) => _now = _now.Add(period);
    }
}
