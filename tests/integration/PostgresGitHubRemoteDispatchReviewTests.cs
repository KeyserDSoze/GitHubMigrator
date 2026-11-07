using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Real PostgreSQL 17 proof: one transaction, two shared advisory lock
// families, DB clock and five physically stored authority chains.
// No external GitHub calls or destination mutation.
public sealed class PostgresGitHubRemoteDispatchReviewTests(
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
        await GitHubRemoteTransmitHazardSchema.InitializeAsync(services);
    }

    public Task DisposeAsync() => Task.CompletedTask;
    private static string Sha(char c) => new(c, 64);
    private static string Oid(char c) => new(c, 40);
    private static GitHubRemoteTransmitHazardOptions Options(TestCase e) =>
        new(true, e.RepositoryId, "gh-org/sandbox");

    internal sealed record TestCase(
        long RepositoryId, Guid HazardId, Guid GrantId,
        Guid AttemptId, Guid FenceRequestId, Guid LeaseToken,
        Guid Actor, Guid PreparedBy, Guid Reviewer,
        Guid Requester, DateTimeOffset Now,
        GitHubCreateOnlyCandidatePacket Source,
        GitHubRemoteAppPreflightReceipt Preflight,
        GitHubRefObservation Latest)
    {
        public PostgresGitHubRemoteDispatchReviewVerifier Verifier(
            AppDbContext db) => new(db, Options(this));

        public Task<GitHubRemoteCreateOnlyDispatchReview> Verify(
            AppDbContext db, CancellationToken ct = default) =>
            Verifier(db).VerifyAsync(Source, Preflight, Latest,
                HazardId, Actor, ct);
    }

    internal static TestCase NewCase()
    {
        var now = DateTimeOffset.UtcNow;
        var repoId = Random.Shared.NextInt64(
            100_000_000_000L, 900_000_000_000L);
        var prep = Guid.NewGuid();
        var plan = Guid.NewGuid();
        var scope = Guid.NewGuid();
        var fenceRequest = Guid.NewGuid();
        var attempt = Guid.NewGuid();
        var grant = Guid.NewGuid();
        var hazard = Guid.NewGuid();
        var lease = Guid.NewGuid();
        var proposedBy = Guid.NewGuid();
        var reviewedBy = Guid.NewGuid();
        var preparedBy = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var candidate = new GitMirrorRef(
            "refs/heads/new-feature", Oid('b'));
        var candHash = GitImportRehearsalIntegrity.SealRefs(
            "f044-create-only-candidate-refs-v1", [candidate]);
        var packet = new GitHubCreateOnlyPacket(
            prep, plan, scope, Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), repoId,
            "gh-org", "sandbox", Sha('1'), Sha('2'), Sha('3'),
            Sha('4'), Sha('5'), Sha('6'), Sha('7'), Sha('8'),
            Sha('a'), candHash, Sha('9'), 1, 1, 1,
            GitHubCreateOnlyPacketIntegrity.State,
            false, false, false, false);
        var latest = GitHubRefObservationIntegrity.Seal(
            "gh-org", "sandbox", now,
            [new("refs/heads/main", Oid('a'), "commit"),
             new("refs/tags/v1", Oid('c'), "tag")]);
        var baseline = GitHubRemoteRefFenceIntegrity.Fingerprint(
            latest.Refs);
        var preflight = new GitHubRemoteAppPreflightReceipt(
            fenceRequest, 2, repoId, packet.EvidenceSha256, candHash,
            packet.PolicySha256, Sha('c'), baseline, baseline, 2, 1,
            now.AddSeconds(-1), now.AddMinutes(45), Sha('d'),
            GitHubRemoteAppPreflightIntegrity.VerifiedState,
            false, false, false);
        preflight = preflight with {
            EvidenceSha256 = GitHubRemoteAppPreflightIntegrity
                .RecomputeHistoricalEvidenceSha256(
                    preflight, preflight.ObservedAtUtc)
        };
        return new(repoId, hazard, grant, attempt, fenceRequest, lease,
            actor, preparedBy, reviewedBy, proposedBy, now,
            new(packet, [candidate]), preflight, latest);
    }

    internal static async Task SeedAsync(AppDbContext db, TestCase e,
        bool grantAudit = true, bool hazardAudit = true)
    {
        var now = e.Now;
        var p = e.Source.Packet;
        var baseline = e.Preflight.ObservedBaselineSha256;
        var leaseRequester = Guid.NewGuid();
        var attemptReviewer = Guid.NewGuid();
        var fenceRequester = Guid.NewGuid();
        var fenceReviewer = Guid.NewGuid();

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_target_preview_leases (
                repository_id,organization,repository,lease_token,
                generation,actor_id,packet_sha256,state,
                acquired_at_utc,lease_until_utc,updated_at_utc,
                execution_authorized,remote_writes_performed,
                ready_for_execution)
            VALUES ({e.RepositoryId},{"gh-org"},{"sandbox"},
                {e.LeaseToken},{1L},{leaseRequester},
                {p.EvidenceSha256},{"Held"},{now},
                {now.AddSeconds(15)},{now},false,false,false)
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_transfer_attempts (
                id,preparation_id,plan_id,scope_id,repository_id,
                organization,repository,packet_sha256,policy_sha256,
                candidate_set_sha256,candidate_count,policy_observed_at_utc,
                lease_token,lease_generation,requested_by,reviewed_by,
                created_at_utc,reviewed_at_utc,blocked_at_utc,expired_at_utc,
                state,transport_attempted,execution_authorized,
                remote_writes_performed,ready_for_execution)
            VALUES ({e.AttemptId},{p.PreparationId},{p.PlanId},{p.ScopeId},
                {e.RepositoryId},{"gh-org"},{"sandbox"},{p.EvidenceSha256},
                {p.PolicySha256},{p.CandidateSetSha256},{1},{now},
                {e.LeaseToken},{1L},{leaseRequester},{attemptReviewer},
                {now},{now},{now},NULL,
                {"BlockedAtWriteBarrier"},false,false,false,false)
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_remote_sandbox_fences (
                repository_id,request_id,preparation_id,plan_id,scope_id,
                attempt_id,organization,repository,packet_sha256,
                candidate_set_sha256,policy_sha256,token_sha256,
                generation,state,requested_by,reviewed_by,
                deadline_utc,updated_at_utc,execution_authorized,
                remote_writes_performed,ready_for_execution)
            VALUES ({e.RepositoryId},{e.FenceRequestId},
                {p.PreparationId},{p.PlanId},{p.ScopeId},{e.AttemptId},
                {"gh-org"},{"sandbox"},{p.EvidenceSha256},
                {p.CandidateSetSha256},{p.PolicySha256},{Sha('f')},
                {2L},{"ReviewedWriteDisabled"},{fenceRequester},
                {fenceReviewer},{now.AddSeconds(13)},{now},
                false,false,false)
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_remote_test_grants (
                id,repository_id,organization,repository,preparation_id,
                plan_id,scope_id,attempt_id,fence_request_id,
                fence_generation,packet_sha256,candidate_set_sha256,
                original_policy_sha256,baseline_sha256,preflight_sha256,
                cancellation_token_sha256,state,requested_by,reviewed_by,
                deadline_utc,updated_at_utc,execution_authorized,
                remote_writes_performed,ready_for_execution)
            VALUES ({e.GrantId},{e.RepositoryId},{"gh-org"},{"sandbox"},
                {p.PreparationId},{p.PlanId},{p.ScopeId},{e.AttemptId},
                {e.FenceRequestId},{2L},{p.EvidenceSha256},
                {p.CandidateSetSha256},{p.PolicySha256},{baseline},
                {e.Preflight.EvidenceSha256},{Sha('b')},
                {"ReviewedWriteDisabled"},{e.Requester},{e.Reviewer},
                {now.AddSeconds(12)},{now},false,false,false)
            """);
        if (grantAudit)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO github_remote_test_grant_events (
                    grant_id,type,actor_id,occurred_at_utc)
                VALUES ({e.GrantId},{"Proposed"},{e.Requester},{now}),
                    ({e.GrantId},{"ReviewedWriteDisabled"},{e.Reviewer},{now})
                """);
        }
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_remote_transmit_hazards (
                id,grant_id,repository_id,fence_request_id,
                fence_generation,packet_sha256,candidate_set_sha256,
                baseline_sha256,abort_token_sha256,state,prepared_by,
                marked_by,deadline_utc,updated_at_utc,
                transport_authorized,transport_invoked,
                remote_writes_performed,may_automatically_retry)
            VALUES ({e.HazardId},{e.GrantId},{e.RepositoryId},
                {e.FenceRequestId},{2L},{p.EvidenceSha256},
                {p.CandidateSetSha256},{baseline},{Sha('f')},
                {"PreparedWriteDisabled"},{e.PreparedBy},NULL,
                {now.AddSeconds(12)},{now},false,false,false,false)
            """);
        if (hazardAudit)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO github_remote_transmit_hazard_events (
                    hazard_id,type,actor_id,occurred_at_utc)
                VALUES ({e.HazardId},{"PreparedWriteDisabled"},
                    {e.PreparedBy},{now})
                """);
        }
    }

    [Fact]
    public async Task F075_database_lock_and_clock_seal_persisted_authority_not_host_DTOs()
    {
        var e = NewCase();
        await using var db = fixture.NewContext();
        await SeedAsync(db, e);
        var receipt = await e.Verify(db);
        var replay = await e.Verify(db);
        Assert.Equal(receipt, replay);
        Assert.Equal(GitHubRemoteCreateOnlyDispatchIntegrity.State,
            receipt.State);
        Assert.Equal(e.GrantId, receipt.GrantId);
        Assert.Equal(e.HazardId, receipt.HazardId);
        Assert.Equal(e.RepositoryId, receipt.RepositoryId);
        Assert.Equal(64, receipt.EvidenceSha256.Length);
        Assert.Equal(1, receipt.AbsentCreateCandidates);
        Assert.Equal(2, receipt.ExistingPreservedRefs);
        Assert.False(receipt.RemoteTransportInvoked);
        Assert.False(receipt.RemoteWritesAuthorized);
        Assert.False(receipt.MayAutomaticallyRetry);
        Assert.False(receipt.MigrationComplete);
        Assert.Equal("PreparedWriteDisabled",
            (await db.GitHubRemoteTransmitHazards.AsNoTracking()
                .SingleAsync(x => x.Id == e.HazardId)).State);
        Assert.Equal(1, await db.GitHubRemoteTransmitHazardEvents
            .AsNoTracking().CountAsync(x => x.HazardId == e.HazardId));
        Assert.Equal(2, await db.GitHubRemoteTestGrantEvents
            .AsNoTracking().CountAsync(x => x.GrantId == e.GrantId));
    }

    [Theory]
    [InlineData("lease-expired")]
    [InlineData("lease-replaced")]
    [InlineData("lease-released")]
    [InlineData("fence-recovery")]
    [InlineData("grant-cancelled")]
    [InlineData("hazard-may-have-transmitted")]
    [InlineData("hazard-review-required")]
    public async Task F075_persisted_lease_and_authority_changes_block_cached_clean_proof(
        string mutation)
    {
        var e = NewCase();
        await using var db = fixture.NewContext();
        await SeedAsync(db, e);
        _ = await e.Verify(db);
        var now = e.Now;
        switch (mutation)
        {
            case "lease-expired":
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE github_target_preview_leases
                    SET lease_until_utc={now.AddMilliseconds(100)},
                        updated_at_utc={now}
                    WHERE repository_id={e.RepositoryId}
                    """);
                await Task.Delay(150);
                break;
            case "lease-replaced":
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE github_target_preview_leases
                    SET lease_token={Guid.NewGuid()}
                    WHERE repository_id={e.RepositoryId}
                    """);
                break;
            case "lease-released":
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE github_target_preview_leases
                    SET state='Released', lease_until_utc=NULL
                    WHERE repository_id={e.RepositoryId}
                    """);
                break;
            case "fence-recovery":
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE github_remote_sandbox_fences
                    SET state='RecoveryRequired'
                    WHERE repository_id={e.RepositoryId}
                    """);
                break;
            case "grant-cancelled":
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE github_remote_test_grants SET state='Cancelled'
                    WHERE id={e.GrantId}
                    """);
                break;
            case "hazard-may-have-transmitted":
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE github_remote_transmit_hazards
                    SET state='MayHaveTransmitted',marked_by={e.PreparedBy}
                    WHERE id={e.HazardId}
                    """);
                break;
            case "hazard-review-required":
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE github_remote_transmit_hazards
                    SET state='OperatorReviewRequired'
                    WHERE id={e.HazardId}
                    """);
                break;
        }
        var error = await Assert.ThrowsAsync<GitHubRemoteCreateOnlyDispatchException>(
            () => e.Verify(db));
        Assert.Equal("RemoteDispatchPersistedAuthorityChanged",
            error.Code);
    }

    [Fact]
    public async Task F075_missing_durable_review_events_rejects_a_plausible_row()
    {
        var e = NewCase();
        await using var db = fixture.NewContext();
        await SeedAsync(db, e, grantAudit: false, hazardAudit: false);
        var error = await Assert.ThrowsAsync<GitHubRemoteCreateOnlyDispatchException>(
            () => e.Verify(db));
        Assert.Equal("RemoteDispatchPersistedAuthorityChanged", error.Code);
    }

    [Fact]
    public async Task F075_six_replicas_can_review_the_same_read_only_epoch_without_writes()
    {
        var e = NewCase();
        await using (var setup = fixture.NewContext())
            await SeedAsync(setup, e);
        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(async _ =>
            {
                await using var replica = fixture.NewContext();
                return await e.Verify(replica);
            }));
        Assert.All(results, receipt =>
        {
            Assert.Equal(results[0].EvidenceSha256, receipt.EvidenceSha256);
            Assert.False(receipt.RemoteWritesAuthorized);
            Assert.False(receipt.RemoteTransportInvoked);
        });
        await using var db = fixture.NewContext();
        Assert.Single(await db.GitHubRemoteTransmitHazardEvents.AsNoTracking()
            .Where(x => x.HazardId == e.HazardId).ToArrayAsync());
        Assert.Equal("PreparedWriteDisabled",
            (await db.GitHubRemoteTransmitHazards.AsNoTracking()
                .SingleAsync(x => x.Id == e.HazardId)).State);
    }

    [Fact]
    public async Task F075_locked_competitor_mark_wins_before_review_and_blocks_it()
    {
        var e = NewCase();
        await using (var setup = fixture.NewContext())
            await SeedAsync(setup, e);
        await using var blocker = fixture.NewContext();
        await using var tx = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(733050, hashtext({e.RepositoryId.ToString()}))");

        var pending = Task.Run(async () =>
        {
            await using var competing = fixture.NewContext();
            return await e.Verify(competing);
        });
        await Task.Delay(150);
        Assert.False(pending.IsCompleted);

        // A concurrent worker marks possible transmission under the
        // exact F-055 repository lock before the F-075 reviewer gets in.
        await blocker.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE github_remote_transmit_hazards
            SET state='MayHaveTransmitted', marked_by={e.PreparedBy}
            WHERE id={e.HazardId}
            """);
        await blocker.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_remote_transmit_hazard_events
                (hazard_id,type,actor_id,occurred_at_utc)
            VALUES ({e.HazardId},{"MayHaveTransmitted"},
                {e.PreparedBy},clock_timestamp())
            """);
        await tx.CommitAsync();

        var error = await Assert.ThrowsAsync<GitHubRemoteCreateOnlyDispatchException>(
            async () => await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("RemoteDispatchPersistedAuthorityChanged",
            error.Code);
        await using var confirm = fixture.NewContext();
        Assert.Equal(GitHubRemoteTransmitHazardStates.MayHaveTransmitted,
            (await confirm.GitHubRemoteTransmitHazards.AsNoTracking()
                .SingleAsync(x => x.Id == e.HazardId)).State);
        Assert.Equal(2, await confirm.GitHubRemoteTransmitHazardEvents
            .AsNoTracking().CountAsync(x => x.HazardId == e.HazardId));
    }
}
