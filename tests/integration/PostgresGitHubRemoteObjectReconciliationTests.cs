using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// PostgreSQL 17: six-replica CAS, composite provenance FKs, immutable
// evidence, frozen MayHaveTransmitted and historical grant expiry.
public sealed class PostgresGitHubRemoteObjectReconciliationTests(
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
        await GitHubRemoteTransmitHazardSchema.InitializeAsync(provider);
        await GitHubRemoteObjectReconciliationSchema.InitializeAsync(provider);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Six_replicas_commit_one_receipt_and_one_append_only_audit_event()
    {
        var f = new RemoteObjectReconciliationFixture();
        await using (var setup = fixture.NewContext())
            await f.SeedAsync(setup);

        async Task<GitHubRemoteObjectReconciliationStatus> Record()
        {
            await using var db = fixture.NewContext();
            return await f.Journal(db).RecordAsync(f.Evidence, default);
        }
        var results = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => Record()));
        var id = results[0].Id;
        Assert.All(results, x =>
        {
            Assert.Equal(id, x.Id);
            Assert.Equal(GitHubRemoteObjectReconciliationStates.FullCopyEvidenceOnly,
                x.State);
            Assert.False(x.RemotePushInvoked);
            Assert.False(x.RemoteWritesAuthorized);
            Assert.False(x.MayAutomaticallyRetry);
            Assert.False(x.MigrationComplete);
            Assert.Equal(f.Evidence.MirrorRequest.SourceSnapshotId,
                x.SourceSnapshotId);
        });

        await using var read = fixture.NewContext();
        Assert.Single(await read.GitHubRemoteObjectReconciliations.AsNoTracking()
            .Where(x => x.HazardId == f.Evidence.HazardId).ToArrayAsync());
        var events = await f.Journal(read).EventsAsync(
            f.Evidence.HazardId, f.Evidence.RepositoryId, default);
        Assert.Single(events);
        Assert.Equal("Recorded", events[0].Type);
        var status = await f.Journal(read).GetAsync(
            f.Evidence.HazardId, f.Evidence.RepositoryId, default);
        Assert.Equal(id, status!.Id);
        Assert.Equal(GitHubRemoteTransmitHazardStates.MayHaveTransmitted,
            (await read.GitHubRemoteTransmitHazards.AsNoTracking()
                .SingleAsync(x => x.Id == f.Evidence.HazardId)).State);
        Assert.DoesNotContain("refs/",
            System.Text.Json.JsonSerializer.Serialize(status));
        Assert.DoesNotContain("x-access-token",
            System.Text.Json.JsonSerializer.Serialize(status));
    }

    [Fact]
    public async Task Conflicting_observer_cannot_overwrite_a_SHA_bound_remote_receipt()
    {
        var f = new RemoteObjectReconciliationFixture();
        await using var db = fixture.NewContext();
        await f.SeedAsync(db);
        var first = await f.Journal(db).RecordAsync(f.Evidence, default);

        var conflicting = f.Evidence with { ObserverId = Guid.NewGuid() };
        var ex = await Assert.ThrowsAsync<GitHubRemoteObjectReconciliationException>(
            () => f.Journal(db).RecordAsync(conflicting, default));
        Assert.Equal("RemoteObjectReconciliationConflict", ex.Code);
        Assert.Equal(first.EvidenceSha256,
            (await f.Journal(db).GetAsync(
                f.Evidence.HazardId, f.Evidence.RepositoryId, default))!
                .EvidenceSha256);
        Assert.Single(await f.Journal(db).EventsAsync(
            f.Evidence.HazardId, f.Evidence.RepositoryId, default));
    }

    [Fact]
    public async Task F050_revocation_or_non_marked_F055_epoch_fails_closed()
    {
        var f = new RemoteObjectReconciliationFixture();
        await using var db = fixture.NewContext();
        await f.SeedAsync(db);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE github_remote_sandbox_fences SET state='Released'
            WHERE repository_id={f.Evidence.RepositoryId}
            """);
        var ex = await Assert.ThrowsAsync<GitHubRemoteObjectReconciliationException>(
            () => f.Journal(db).RecordAsync(f.Evidence, default));
        Assert.Equal("RemoteObjectReconciliationBarrierChanged", ex.Code);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE github_remote_sandbox_fences SET state='ReviewedWriteDisabled'
            WHERE repository_id={f.Evidence.RepositoryId}
            """);
        // The F055 row is protected against rolling back to Prepared.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE github_remote_transmit_hazards
                SET state='PreparedWriteDisabled'
                WHERE id={f.Evidence.HazardId}
                """));

        Assert.Equal(0, await db.GitHubRemoteObjectReconciliations.CountAsync(
            x => x.HazardId == f.Evidence.HazardId));
        var stored = await f.Journal(db).RecordAsync(f.Evidence, default);
        Assert.False(stored.MayAutomaticallyRetry);
    }

    [Fact]
    public async Task Direct_SQL_cannot_authorize_push_retry_or_edit_hashes_and_events()
    {
        var f = new RemoteObjectReconciliationFixture();
        await using var db = fixture.NewContext();
        await f.SeedAsync(db);
        var receipt = await f.Journal(db).RecordAsync(f.Evidence, default);

        Func<Task>[] forbidden = [
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE github_remote_object_reconciliations SET remote_push_invoked={true} WHERE id={receipt.Id}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE github_remote_object_reconciliations SET remote_writes_authorized={true} WHERE id={receipt.Id}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE github_remote_object_reconciliations SET may_automatically_retry={true} WHERE id={receipt.Id}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE github_remote_object_reconciliations SET migration_complete={true} WHERE id={receipt.Id}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE github_remote_object_reconciliations SET full_object_closure_sha256={RemoteObjectReconciliationFixture.Sha('f')} WHERE id={receipt.Id}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM github_remote_object_reconciliations WHERE id={receipt.Id}"),
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM github_remote_object_reconciliation_events WHERE reconciliation_id={receipt.Id}")
        ];
        foreach (var write in forbidden)
            await Assert.ThrowsAnyAsync<Exception>(write);

        Assert.Equal(receipt.EvidenceSha256,
            (await f.Journal(db).GetAsync(
                f.Evidence.HazardId, f.Evidence.RepositoryId, default))!
                .EvidenceSha256);
        Assert.Single(await f.Journal(db).EventsAsync(
            f.Evidence.HazardId, f.Evidence.RepositoryId, default));
    }

    [Fact]
    public async Task Grant_hazard_readback_composite_FKs_reject_fabricated_lineage()
    {
        var f = new RemoteObjectReconciliationFixture();
        await using var db = fixture.NewContext();
        await f.SeedAsync(db);
        var receipt = await f.Journal(db).RecordAsync(f.Evidence, default);
        var fakeHazard = Guid.NewGuid();

        // A byte-identical, otherwise valid proof cannot be attached
        // to a different F055 hazard ID directly in SQL.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO github_remote_object_reconciliations
                (id,grant_id,readback_id,hazard_id,repository_id,
                 source_snapshot_id,state,original_baseline_sha256,
                 candidate_set_sha256,target_ref_set_sha256,
                 remote_ref_observation_sha256,full_object_closure_sha256,
                 remote_mirror_receipt_sha256,evidence_sha256,
                 observed_by,verified_at_utc,recorded_at_utc,
                 remote_push_invoked,remote_writes_authorized,
                 may_automatically_retry,migration_complete)
                SELECT {Guid.NewGuid()},grant_id,readback_id,{fakeHazard},
                       repository_id,source_snapshot_id,state,
                       original_baseline_sha256,candidate_set_sha256,
                       target_ref_set_sha256,remote_ref_observation_sha256,
                       full_object_closure_sha256,remote_mirror_receipt_sha256,
                       evidence_sha256,observed_by,verified_at_utc,
                       recorded_at_utc,false,false,false,false
                FROM github_remote_object_reconciliations
                WHERE id={receipt.Id}
                """));
        Assert.Equal(1, await db.GitHubRemoteObjectReconciliations.CountAsync(
            x => x.GrantId == f.Evidence.GrantId));
    }
}
