using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Integration.Tests;

// Synthetic F-057 domain-sealed proof fixture: no GitHub network, no
// transport and no claim of a real remote sender. Real F-057 Git
// acquisition is tested independently in F-057 integration tests.
internal sealed class RemoteObjectReconciliationFixture
{
    public GitHubRemoteObjectReconciliationEvidence Evidence { get; }
    public GitHubReadOnlyObjectMirrorOptions Options { get; }
    public DateTimeOffset Now { get; }
    public Guid AttemptId { get; } = Guid.NewGuid();
    public Guid FenceId { get; } = Guid.NewGuid();
    public Guid PreparationId { get; } = Guid.NewGuid();
    public Guid PlanId { get; } = Guid.NewGuid();
    public Guid ScopeId { get; } = Guid.NewGuid();

    public RemoteObjectReconciliationFixture()
    {
        Now = DateTimeOffset.UtcNow;
        var repo = Random.Shared.NextInt64(
            100_000_000_000L, 900_000_000_000L);
        GitHubObservedRef[] baseline = [
            new("refs/heads/main", Oid('a'), "commit"),
            new("refs/tags/v1", Oid('b'), "tag")
        ];
        GitMirrorRef[] creates = [
            new("refs/heads/feature", Oid('c')),
            new("refs/tags/v2", Oid('d'))
        ];
        var remote = GitHubRefObservationIntegrity.Seal(
            "gh-org", "test-sandbox", Now, [
                new("refs/heads/feature", Oid('c'), "commit"),
                new("refs/heads/main", Oid('a'), "commit"),
                new("refs/tags/v1", Oid('b'), "tag"),
                new("refs/tags/v2", Oid('d'), "tag")
            ]);
        var request = new GitHubReadOnlyObjectMirrorRequest(
            Guid.NewGuid(), repo, "gh-org", "test-sandbox",
            remote.AsMirrorRefs(), remote, Now.AddMinutes(40));
        var snapshot = Guid.NewGuid();
        var closure = GitObjectClosureAuditIntegrity.Seal(new(
            request.SourceSnapshotId, snapshot, request.ExpectedSourceRefs,
            remote.AsMirrorRefs()), "sha1",
            Sha('e'), Sha('e'), 16, 16, Now);
        Options = new(true, repo, "gh-org/test-sandbox");
        var receipt = GitHubReadOnlyObjectMirrorIntegrity.Seal(
            request, new(snapshot, remote.AsMirrorRefs(), closure),
            Options, Now);
        Evidence = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), repo,
            Guid.NewGuid(), baseline, creates, request, receipt);
    }

    public PostgresGitHubRemoteObjectReconciliationJournal Journal(
        AppDbContext db) => new(db, TimeProvider.System, Options);

    public static string Sha(char c) => new(c, 64);
    private static string Oid(char c) => new(c, 40);

    public async Task SeedAsync(AppDbContext db)
    {
        var e = Evidence;
        var now = Now.AddSeconds(-3);
        var proof = GitHubRemoteObjectReconciliationIntegrity.Verify(
            e, Options, Now);
        var policy = Sha('5');
        var packet = Sha('6');
        var requestedF047 = Guid.NewGuid();
        var reviewedF047 = Guid.NewGuid();
        var requestedF050 = Guid.NewGuid();
        var reviewedF050 = Guid.NewGuid();
        var requestedF052 = Guid.NewGuid();
        var reviewedF052 = Guid.NewGuid();
        var observedF054 = Guid.NewGuid();
        var preparedF055 = Guid.NewGuid();
        var markedF055 = Guid.NewGuid();

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_transfer_attempts (
                id, preparation_id, plan_id, scope_id, repository_id,
                organization, repository, packet_sha256, policy_sha256,
                candidate_set_sha256, candidate_count, policy_observed_at_utc,
                lease_token, lease_generation, requested_by, reviewed_by,
                created_at_utc, reviewed_at_utc, blocked_at_utc, expired_at_utc,
                state, transport_attempted, execution_authorized,
                remote_writes_performed, ready_for_execution)
            VALUES ({AttemptId},{PreparationId},{PlanId},{ScopeId},
                {e.RepositoryId},{"gh-org"},{"test-sandbox"},
                {packet},{policy},{proof.CandidateSetSha256},{2},
                {now},{Guid.NewGuid()},{1L},{requestedF047},{reviewedF047},
                {now},{now},{now},NULL,
                'BlockedAtWriteBarrier',false,false,false,false)
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_remote_sandbox_fences (
                repository_id, request_id, preparation_id, plan_id,
                scope_id, attempt_id, organization, repository,
                packet_sha256, candidate_set_sha256, policy_sha256,
                token_sha256, generation, state, requested_by,
                reviewed_by, deadline_utc, updated_at_utc,
                execution_authorized, remote_writes_performed,
                ready_for_execution)
            VALUES ({e.RepositoryId},{FenceId},{PreparationId},{PlanId},
                {ScopeId},{AttemptId},{"gh-org"},{"test-sandbox"},
                {packet},{proof.CandidateSetSha256},{policy},{Sha('7')},{1L},
                'ReviewedWriteDisabled',{requestedF050},{reviewedF050},
                {now.AddSeconds(1)},{now},false,false,false)
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_remote_test_grants (
                id, repository_id, organization, repository,
                preparation_id, plan_id, scope_id, attempt_id,
                fence_request_id, fence_generation, packet_sha256,
                candidate_set_sha256, original_policy_sha256,
                baseline_sha256, preflight_sha256, cancellation_token_sha256,
                state, requested_by, reviewed_by, deadline_utc,
                updated_at_utc, execution_authorized,
                remote_writes_performed, ready_for_execution)
            VALUES ({e.GrantId},{e.RepositoryId},{"gh-org"},{"test-sandbox"},
                {PreparationId},{PlanId},{ScopeId},{AttemptId},{FenceId},
                {1L},{packet},{proof.CandidateSetSha256},{policy},
                {proof.OriginalBaselineSha256},{Sha('8')},{Sha('9')},
                'ReviewedWriteDisabled',{requestedF052},{reviewedF052},
                {now.AddSeconds(1)},{now},false,false,false)
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_remote_readback_evidence (
                id, grant_id, repository_id, state, baseline_sha256,
                candidate_set_sha256, ref_set_sha256,
                tip_evidence_sha256, assessment_sha256,
                journal_evidence_sha256, observed_by, observed_at_utc,
                recorded_at_utc, transport_attempted,
                remote_writes_authorized, may_automatically_retry)
            VALUES ({e.ReadbackId},{e.GrantId},{e.RepositoryId},
                'ExactTipEvidenceOnly',{proof.OriginalBaselineSha256},
                {proof.CandidateSetSha256},{proof.TargetRefSetSha256},
                {Sha('a')},{Sha('b')},{Sha('c')},{observedF054},{now},
                {now},false,false,false)
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_remote_readback_events (
                readback_id, type, actor_id, occurred_at_utc)
            VALUES ({e.ReadbackId},'Recorded',{observedF054},{now})
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_remote_transmit_hazards (
                id, grant_id, repository_id, fence_request_id,
                fence_generation, packet_sha256, candidate_set_sha256,
                baseline_sha256, abort_token_sha256, state, prepared_by,
                marked_by, deadline_utc, updated_at_utc,
                transport_authorized, transport_invoked,
                remote_writes_performed, may_automatically_retry)
            VALUES ({e.HazardId},{e.GrantId},{e.RepositoryId},{FenceId},
                {1L},{packet},{proof.CandidateSetSha256},
                {proof.OriginalBaselineSha256},{Sha('d')},
                'MayHaveTransmitted',{preparedF055},{markedF055},
                {now.AddSeconds(1)},{now},
                false,false,false,false)
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_remote_transmit_hazard_events (
                hazard_id,type,actor_id,occurred_at_utc)
            VALUES ({e.HazardId},'MayHaveTransmitted',{markedF055},{now})
            """);
    }
}
