using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// F-077 uses actual PostgreSQL17 constraints, immutable triggers, a
// F-050 repository advisory transaction lock and a reviewer-bound
// F-052 preflight digest. No GitHub network calls, tokens or writes.
public sealed class PostgresGitHubRemoteAppPreflightEvidenceTests(
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
        await GitHubRemoteTransmitHazardSchema.InitializeAsync(provider);
        await GitHubRemoteAppPreflightEvidenceSchema.InitializeAsync(provider);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Sha(char c) => new(c, 64);

    private sealed record Case(
        long RepositoryId, Guid FenceRequestId, Guid AttemptId,
        Guid PreparationId, Guid PlanId, Guid ScopeId,
        Guid RequestedBy, Guid ReviewedBy,
        DateTimeOffset Clock,
        GitHubRemoteAppPreflightReceipt Proof)
    {
        public GitHubRemoteSandboxFenceOptions Options =>
            new(true, RepositoryId, "gh-org/sandbox");

        public PostgresGitHubRemoteAppPreflightEvidenceJournal Store(
            AppDbContext db) => new(db, Options);

        public GitHubRemoteAppPreflightReceipt Another(int milliseconds)
        {
            var changed = Proof with {
                ObservedAtUtc = Proof.ObservedAtUtc.AddMilliseconds(milliseconds),
                InstallationTokenExpiresAtUtc =
                    Proof.InstallationTokenExpiresAtUtc.AddMilliseconds(milliseconds)
            };
            return changed with {
                EvidenceSha256 = GitHubRemoteAppPreflightIntegrity
                    .RecomputeHistoricalEvidenceSha256(
                        changed, changed.ObservedAtUtc)
            };
        }
    }

    private static Case NewCase()
    {
        var now = DateTimeOffset.UtcNow;
        var repo = Random.Shared.NextInt64(
            300_000_000_000L, 950_000_000_000L);
        var p = new GitHubRemoteAppPreflightReceipt(
            Guid.NewGuid(), 2, repo,
            Sha('a'), Sha('b'), Sha('c'), Sha('d'),
            Sha('e'), Sha('e'), 2, 1, now,
            now.AddMinutes(45), Sha('f'),
            GitHubRemoteAppPreflightIntegrity.VerifiedState,
            false, false, false);
        var digest = GitHubRemoteAppPreflightIntegrity
            .RecomputeHistoricalEvidenceSha256(p, now);
        return new(repo, p.RequestId, Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            now, p with { EvidenceSha256 = digest });
    }

    private static async Task SeedFenceAsync(AppDbContext db, Case c)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_transfer_attempts (
                id,preparation_id,plan_id,scope_id,repository_id,
                organization,repository,packet_sha256,policy_sha256,
                candidate_set_sha256,candidate_count,policy_observed_at_utc,
                lease_token,lease_generation,requested_by,reviewed_by,
                created_at_utc,reviewed_at_utc,blocked_at_utc,expired_at_utc,
                state,transport_attempted,execution_authorized,
                remote_writes_performed,ready_for_execution)
            VALUES ({c.AttemptId},{c.PreparationId},{c.PlanId},{c.ScopeId},
                {c.RepositoryId},{"gh-org"},{"sandbox"},
                {c.Proof.PacketSha256},{c.Proof.OriginalPolicySha256},
                {c.Proof.CandidateSetSha256},{1},{c.Clock},
                {Guid.NewGuid()},{1L},{c.RequestedBy},{c.ReviewedBy},
                {c.Clock},{c.Clock},{c.Clock},NULL,
                {"BlockedAtWriteBarrier"},false,false,false,false)
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_remote_sandbox_fences (
                repository_id,request_id,preparation_id,plan_id,scope_id,
                attempt_id,organization,repository,packet_sha256,
                candidate_set_sha256,policy_sha256,token_sha256,
                generation,state,requested_by,reviewed_by,deadline_utc,
                updated_at_utc,execution_authorized,remote_writes_performed,
                ready_for_execution)
            VALUES ({c.RepositoryId},{c.FenceRequestId},
                {c.PreparationId},{c.PlanId},{c.ScopeId},{c.AttemptId},
                {"gh-org"},{"sandbox"},{c.Proof.PacketSha256},
                {c.Proof.CandidateSetSha256},
                {c.Proof.OriginalPolicySha256},{Sha('f')},
                {2L},{"ReviewedWriteDisabled"},
                {c.RequestedBy},{c.ReviewedBy},
                {c.Clock.AddMinutes(3)},{c.Clock},false,false,false)
            """);
    }

    private static async Task<Guid> SeedGrantAndHazardAsync(
        AppDbContext db, Case c, string pinnedSha)
    {
        var grantId = Guid.NewGuid();
        var hazardId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_remote_test_grants (
                id,repository_id,organization,repository,preparation_id,
                plan_id,scope_id,attempt_id,fence_request_id,
                fence_generation,packet_sha256,candidate_set_sha256,
                original_policy_sha256,baseline_sha256,preflight_sha256,
                cancellation_token_sha256,state,requested_by,reviewed_by,
                deadline_utc,updated_at_utc,execution_authorized,
                remote_writes_performed,ready_for_execution)
            VALUES ({grantId},{c.RepositoryId},{"gh-org"},{"sandbox"},
                {c.PreparationId},{c.PlanId},{c.ScopeId},{c.AttemptId},
                {c.FenceRequestId},{2L},{c.Proof.PacketSha256},
                {c.Proof.CandidateSetSha256},
                {c.Proof.OriginalPolicySha256},
                {c.Proof.ObservedBaselineSha256},{pinnedSha},{Sha('f')},
                {"ReviewedWriteDisabled"},{c.RequestedBy},{c.ReviewedBy},
                {c.Clock.AddMinutes(3)},{c.Clock},false,false,false)
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_remote_transmit_hazards (
                id,grant_id,repository_id,fence_request_id,
                fence_generation,packet_sha256,candidate_set_sha256,
                baseline_sha256,abort_token_sha256,state,prepared_by,
                marked_by,deadline_utc,updated_at_utc,
                transport_authorized,transport_invoked,
                remote_writes_performed,may_automatically_retry)
            VALUES ({hazardId},{grantId},{c.RepositoryId},
                {c.FenceRequestId},{2L},{c.Proof.PacketSha256},
                {c.Proof.CandidateSetSha256},
                {c.Proof.ObservedBaselineSha256},{Sha('f')},
                {"PreparedWriteDisabled"},{Guid.NewGuid()},NULL,
                {c.Clock.AddMinutes(3)},{c.Clock},false,false,false,false)
            """);
        return hazardId;
    }

    [Fact]
    public async Task F077_proposal_and_review_preflights_both_remain_immutable_and_hash_addressable()
    {
        var c = NewCase();
        await using var db = fixture.NewContext();
        await SeedFenceAsync(db, c);
        var store = c.Store(db);
        var proposed = await store.RecordAsync(c.Proof,
            c.Proof.ObservedAtUtc, default);
        var reviewedProof = c.Another(25);
        var reviewed = await store.RecordAsync(reviewedProof,
            reviewedProof.ObservedAtUtc, default);
        Assert.NotEqual(proposed.EvidenceSha256, reviewed.EvidenceSha256);
        Assert.Equal(c.FenceRequestId, reviewed.RequestId);
        Assert.Equal(2L, reviewed.Generation);

        var first = await store.GetAsync(c.FenceRequestId, 2,
            c.RepositoryId, proposed.EvidenceSha256, default);
        var second = await store.GetAsync(c.FenceRequestId, 2,
            c.RepositoryId, reviewed.EvidenceSha256, default);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(proposed.EvidenceSha256, first.EvidenceSha256);
        Assert.Equal(reviewed.EvidenceSha256, second.EvidenceSha256);
        Assert.Null(await store.GetAsync(c.FenceRequestId, 2,
            c.RepositoryId, Sha('f'), default));
        Assert.Equal(2, await db.GitHubRemoteAppPreflightEvidence
            .AsNoTracking().CountAsync(x => x.RequestId == c.FenceRequestId));
        Assert.Equal(2, await db.GitHubRemoteAppPreflightEvents.AsNoTracking()
            .CountAsync(x => x.RequestId == c.FenceRequestId));
        Assert.False(first.RemoteWritesPerformed);
        Assert.False(second.ExecutionAuthorized);
        Assert.False(second.ReadyForExecution);
    }

    [Fact]
    public async Task F077_six_replica_replays_append_only_one_receipt_and_one_event()
    {
        var c = NewCase();
        await using (var setup = fixture.NewContext())
            await SeedFenceAsync(setup, c);
        var proofs = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(async _ =>
            {
                await using var db = fixture.NewContext();
                return await c.Store(db).RecordAsync(
                    c.Proof, c.Proof.ObservedAtUtc, default);
            }));
        Assert.All(proofs, p =>
            Assert.Equal(c.Proof.EvidenceSha256, p.EvidenceSha256));
        await using var check = fixture.NewContext();
        Assert.Equal(1, await check.GitHubRemoteAppPreflightEvidence
            .AsNoTracking().CountAsync(x => x.RequestId == c.FenceRequestId));
        Assert.Equal(1, await check.GitHubRemoteAppPreflightEvents
            .AsNoTracking().CountAsync(x => x.RequestId == c.FenceRequestId));
    }

    [Fact]
    public async Task F077_different_payload_in_same_digest_cannot_replace_any_epoch()
    {
        var c = NewCase();
        await using var db = fixture.NewContext();
        await SeedFenceAsync(db, c);
        var store = c.Store(db);
        await store.RecordAsync(c.Proof, c.Proof.ObservedAtUtc, default);
        var altered = c.Proof with { AbsentCreateCandidates = 2 };
        var error = await Assert.ThrowsAsync<GitHubRemoteAppPreflightException>(
            () => store.RecordAsync(altered, c.Proof.ObservedAtUtc, default));
        Assert.Equal("RemotePreflightJournalIntegrityMismatch", error.Code);
        Assert.Equal(1, await db.GitHubRemoteAppPreflightEvidence
            .AsNoTracking().CountAsync(x => x.RequestId == c.FenceRequestId));
    }

    [Fact]
    public async Task F077_fence_changed_before_commit_blocks_any_new_attestation()
    {
        var c = NewCase();
        await using var db = fixture.NewContext();
        await SeedFenceAsync(db, c);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE github_remote_sandbox_fences
            SET state='RecoveryRequired'
            WHERE repository_id={c.RepositoryId}
            """);
        var err = await Assert.ThrowsAsync<GitHubRemoteAppPreflightException>(
            () => c.Store(db).RecordAsync(
                c.Proof, c.Proof.ObservedAtUtc, default));
        Assert.Equal("RemotePreflightJournalFenceChanged", err.Code);
        Assert.Equal(0, await db.GitHubRemoteAppPreflightEvents
            .AsNoTracking().CountAsync(x => x.RequestId == c.FenceRequestId));
    }

    [Fact]
    public async Task F077_stored_rows_and_audit_events_reject_update_delete_and_false_write_flag()
    {
        var c = NewCase();
        await using var db = fixture.NewContext();
        await SeedFenceAsync(db, c);
        await c.Store(db).RecordAsync(c.Proof, c.Proof.ObservedAtUtc, default);
        foreach (var sql in new[] {
            "UPDATE github_remote_app_preflight_evidence SET current_policy_sha256='ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff' WHERE request_id=@request",
            "DELETE FROM github_remote_app_preflight_evidence WHERE request_id=@request",
            "UPDATE github_remote_app_preflight_evidence SET execution_authorized=true WHERE request_id=@request",
            "DELETE FROM github_remote_app_preflight_events WHERE request_id=@request"
        })
        {
            await Assert.ThrowsAsync<PostgresException>(async () =>
                await db.Database.ExecuteSqlRawAsync(sql,
                    new NpgsqlParameter("request", c.FenceRequestId)));
        }
        var immutable = await c.Store(db).GetAsync(
            c.FenceRequestId, 2, c.RepositoryId,
            c.Proof.EvidenceSha256, default);
        Assert.NotNull(immutable);
        Assert.Equal(c.Proof.EvidenceSha256, immutable.EvidenceSha256);
        Assert.Single(await db.GitHubRemoteAppPreflightEvents.AsNoTracking()
            .Where(x => x.RequestId == c.FenceRequestId).ToArrayAsync());
    }

    [Fact]
    public async Task F077_F055_hazard_loads_only_the_F052_reviewed_preflight_sha()
    {
        var c = NewCase();
        await using var db = fixture.NewContext();
        await SeedFenceAsync(db, c);
        var store = c.Store(db);
        await store.RecordAsync(c.Proof, c.Proof.ObservedAtUtc, default);
        var final = c.Another(17);
        await store.RecordAsync(final, final.ObservedAtUtc, default);
        var hazard = await SeedGrantAndHazardAsync(db, c,
            final.EvidenceSha256);
        var chosen = await store.GetForHazardAsync(hazard, default);
        Assert.NotNull(chosen);
        Assert.Equal(final.EvidenceSha256, chosen.EvidenceSha256);
        Assert.NotEqual(c.Proof.EvidenceSha256, chosen.EvidenceSha256);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE github_remote_transmit_hazards
            SET state='MayHaveTransmitted', marked_by={Guid.NewGuid()}
            WHERE id={hazard}
            """);
        var error = await Assert.ThrowsAsync<GitHubRemoteAppPreflightException>(
            () => store.GetForHazardAsync(hazard, default));
        Assert.Equal("RemotePreflightJournalHazardChanged", error.Code);
        Assert.NotNull(await store.GetAsync(
            c.FenceRequestId, 2, c.RepositoryId,
            final.EvidenceSha256, default));
    }
}
