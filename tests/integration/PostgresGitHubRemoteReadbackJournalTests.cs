using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Real PostgreSQL 17: immutable audited observations, cross-replica CAS,
// four-actor barrier recheck, no writable or auto-retry state in SQL.
public sealed class PostgresGitHubRemoteReadbackJournalTests(
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
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Concurrent_replica_replay_records_exactly_one_immutable_receipt()
    {
        var evidence = Build();
        await using (var setup = fixture.NewContext())
            await SeedAsync(setup, evidence);

        async Task<GitHubRemoteReadbackStatus> Record()
        {
            await using var db = fixture.NewContext();
            return await Journal(db, evidence.RepositoryId)
                .RecordAsync(evidence, default);
        }
        var results = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => Record()));

        var id = results[0].Id;
        Assert.All(results, x =>
        {
            Assert.Equal(id, x.Id);
            Assert.Equal(GitHubRemoteReadbackStates.ExactTipEvidenceOnly, x.State);
            Assert.False(x.TransportAttempted);
            Assert.False(x.RemoteWritesAuthorized);
            Assert.False(x.MayAutomaticallyRetry);
        });
        await using var verify = fixture.NewContext();
        Assert.Equal(1, await verify.GitHubRemoteReadbacks.CountAsync(
            x => x.GrantId == evidence.GrantId));
        var events = await Journal(verify, evidence.RepositoryId)
            .EventsAsync(evidence.GrantId, evidence.RepositoryId, default);
        Assert.Single(events);
        Assert.Equal("Recorded", events[0].Type);

        var status = await Journal(verify, evidence.RepositoryId)
            .GetAsync(evidence.GrantId, evidence.RepositoryId, default);
        Assert.Equal(id, status!.Id);
        var serialized = System.Text.Json.JsonSerializer.Serialize(status);
        Assert.DoesNotContain("refs/", serialized);
        Assert.DoesNotContain("ghs_", serialized);
    }

    [Fact]
    public async Task Different_provider_evidence_cannot_replace_first_receipt()
    {
        var evidence = Build();
        await using var db = fixture.NewContext();
        await SeedAsync(db, evidence);
        var journal = Journal(db, evidence.RepositoryId);
        var before = await journal.RecordAsync(evidence, default);

        var remote = GitHubRefObservationIntegrity.Seal("gh-org", "sandbox",
            evidence.Snapshot.RemoteRefs.ObservedAtUtc,
            [..evidence.Snapshot.RemoteRefs.Refs,
                new GitHubObservedRef("refs/heads/unexpected",
                    Oid('f'), "commit")]);
        var objectEvidence = GitHubRemoteTipIntegrity.Seal(remote,
            evidence.CreateCandidates, remote.ObservedAtUtc,
            evidence.Snapshot.TipReadback.Tips);
        var conflicting = evidence with {
            Snapshot = new(remote, objectEvidence)
        };
        var ex = await Assert.ThrowsAsync<GitHubRemoteReadbackException>(
            () => journal.RecordAsync(conflicting, default));
        Assert.Equal("RemoteReadbackConflict", ex.Code);
        Assert.Equal(before.JournalEvidenceSha256,
            (await journal.GetAsync(evidence.GrantId,
                evidence.RepositoryId, default))!.JournalEvidenceSha256);
        Assert.Single(await journal.EventsAsync(evidence.GrantId,
            evidence.RepositoryId, default));
    }

    [Fact]
    public async Task Database_triggers_and_checks_permanently_block_remote_authority_and_changes()
    {
        var evidence = Build();
        await using var db = fixture.NewContext();
        await SeedAsync(db, evidence);
        var result = await Journal(db, evidence.RepositoryId)
            .RecordAsync(evidence, default);

        Func<Task>[] forbidden = [
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE github_remote_readback_evidence SET remote_writes_authorized={true} WHERE id={result.Id}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE github_remote_readback_evidence SET transport_attempted={true} WHERE id={result.Id}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE github_remote_readback_evidence SET may_automatically_retry={true} WHERE id={result.Id}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE github_remote_readback_evidence SET state={"OperatorReviewRequired"} WHERE id={result.Id}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM github_remote_readback_evidence WHERE id={result.Id}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM github_remote_readback_events WHERE readback_id={result.Id}")
        ];
        foreach (var write in forbidden)
            await Assert.ThrowsAnyAsync<Exception>(write);

        Assert.Equal(result.JournalEvidenceSha256,
            (await Journal(db, evidence.RepositoryId)
                .GetAsync(evidence.GrantId, evidence.RepositoryId, default))!
                .JournalEvidenceSha256);
        Assert.Single(await Journal(db, evidence.RepositoryId)
            .EventsAsync(evidence.GrantId, evidence.RepositoryId, default));
    }

    [Fact]
    public async Task Missing_F052_review_or_changed_F050_barrier_refuses_recording()
    {
        var evidence = Build();
        await using var db = fixture.NewContext();
        var actors = await SeedAsync(db, evidence);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE github_remote_sandbox_fences
            SET state='Released'
            WHERE repository_id={evidence.RepositoryId}
            """);
        var ex = await Assert.ThrowsAsync<GitHubRemoteReadbackException>(() =>
            Journal(db, evidence.RepositoryId).RecordAsync(evidence, default));
        Assert.Equal("RemoteReadbackBarrierChanged", ex.Code);
        Assert.Equal(0, await db.GitHubRemoteReadbacks.CountAsync(
            x => x.GrantId == evidence.GrantId));

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE github_remote_sandbox_fences
            SET state='ReviewedWriteDisabled'
            WHERE repository_id={evidence.RepositoryId}
            """);
        var wrongObserver = evidence with { ObserverId = actors.F052Reviewer };
        ex = await Assert.ThrowsAsync<GitHubRemoteReadbackException>(() =>
            Journal(db, evidence.RepositoryId)
                .RecordAsync(wrongObserver, default));
        Assert.Equal("RemoteReadbackBarrierChanged", ex.Code);
        Assert.Equal(0, await db.GitHubRemoteReadbacks.CountAsync(
            x => x.GrantId == evidence.GrantId));

        ex = await Assert.ThrowsAsync<GitHubRemoteReadbackException>(() =>
            Journal(db, evidence.RepositoryId + 1)
                .RecordAsync(evidence, default));
        Assert.Equal("RemoteReadbackDisabled", ex.Code);
    }

    private static PostgresGitHubRemoteReadbackJournal Journal(
        AppDbContext db, long repo) =>
        new(db, TimeProvider.System,
            new GitHubRemoteTestGrantOptions(true, repo, "gh-org/sandbox"));

    private static string Oid(char c) => new(c, 40);
    private static string Sha(char c) => new(c, 64);

    private static GitHubRemoteReadbackEvidence Build()
    {
        var repo = Random.Shared.NextInt64(
            100_000_000_000L, 900_000_000_000L);
        var now = DateTimeOffset.UtcNow;
        GitHubObservedRef[] baseline = [
            new("refs/heads/main", Oid('e'), "commit"),
            new("refs/tags/v1", Oid('d'), "tag")
        ];
        GitMirrorRef[] creates = [
            new("refs/heads/feature", Oid('a')),
            new("refs/tags/v2", Oid('b'))
        ];
        var remote = GitHubRefObservationIntegrity.Seal("gh-org",
            "sandbox", now, [
                new("refs/heads/feature", Oid('a'), "commit"),
                new("refs/heads/main", Oid('e'), "commit"),
                new("refs/tags/v1", Oid('d'), "tag"),
                new("refs/tags/v2", Oid('b'), "tag")
            ]);
        var tips = GitHubRemoteTipIntegrity.Seal(
            remote, creates, remote.ObservedAtUtc, [
                new("refs/heads/feature", Oid('a'), "commit",
                    Oid('a'), Oid('c'), 0),
                new("refs/tags/v2", Oid('b'), "tag",
                    Oid('f'), Oid('c'), 1)
            ]);
        return new(Guid.NewGuid(), repo, Guid.NewGuid(),
            baseline, creates, new(remote, tips));
    }

    private static async Task<(Guid F052Reviewer)> SeedAsync(
        AppDbContext db, GitHubRemoteReadbackEvidence evidence)
    {
        var now = DateTimeOffset.UtcNow;
        var decision = GitHubRemoteReadbackIntegrity.Verify(evidence, now);
        var attemptId = Guid.NewGuid();
        var fenceId = Guid.NewGuid();
        var f047Proposer = Guid.NewGuid();
        var f047Reviewer = Guid.NewGuid();
        var f050Proposer = Guid.NewGuid();
        var f050Reviewer = Guid.NewGuid();
        var f052Proposer = Guid.NewGuid();
        var f052Reviewer = Guid.NewGuid();
        var preparation = Guid.NewGuid();
        var plan = Guid.NewGuid();
        var scope = Guid.NewGuid();
        var packet = Sha('1');
        var policy = Sha('2');
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_transfer_attempts (
                id, preparation_id, plan_id, scope_id, repository_id,
                organization, repository, packet_sha256, policy_sha256,
                candidate_set_sha256, candidate_count, policy_observed_at_utc,
                lease_token, lease_generation, requested_by, reviewed_by,
                created_at_utc, reviewed_at_utc, blocked_at_utc, expired_at_utc,
                state, transport_attempted, execution_authorized,
                remote_writes_performed, ready_for_execution)
            VALUES ({attemptId},{preparation},{plan},{scope},
                {evidence.RepositoryId},{"gh-org"},{"sandbox"},
                {packet},{policy},{decision.CandidateSetSha256},
                {2},{now},{Guid.NewGuid()},{1L},
                {f047Proposer},{f047Reviewer},{now},{now},{now},NULL,
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
            VALUES ({evidence.RepositoryId},{fenceId},{preparation},
                {plan},{scope},{attemptId},{"gh-org"},{"sandbox"},
                {packet},{decision.CandidateSetSha256},{policy},
                {Sha('3')},{1L},'ReviewedWriteDisabled',
                {f050Proposer},{f050Reviewer},{now.AddMinutes(2)},{now},
                false,false,false)
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_remote_test_grants (
                id, repository_id, organization, repository,
                preparation_id, plan_id, scope_id, attempt_id,
                fence_request_id, fence_generation,
                packet_sha256, candidate_set_sha256,
                original_policy_sha256, baseline_sha256,
                preflight_sha256, cancellation_token_sha256,
                state, requested_by, reviewed_by,
                deadline_utc, updated_at_utc,
                execution_authorized, remote_writes_performed, ready_for_execution)
            VALUES ({evidence.GrantId},{evidence.RepositoryId},
                {"gh-org"},{"sandbox"},{preparation},{plan},{scope},
                {attemptId},{fenceId},{1L},{packet},
                {decision.CandidateSetSha256},{policy},
                {decision.BaselineSha256},{Sha('4')},{Sha('5')},
                'ReviewedWriteDisabled',{f052Proposer},{f052Reviewer},
                {now.AddMinutes(2)},{now},false,false,false)
            """);
        return (f052Reviewer);
    }
}
