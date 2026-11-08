using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// F-086: a REAL F-081 -> F-082 transaction chain and immutable typed
// ref/tag snapshot. No GitHub credentials, network calls or pushes.
public sealed class PostgresGitHubRemoteTypedRecoveryBaselineTests(
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
        await GitHubRemoteTypedRecoveryBaselineSchema.InitializeAsync(services);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static PostgresGitHubRemoteTypedRecoveryBaselineJournal Journal(
        AppDbContext db, PostgresGitHubRemoteDispatchReviewTests.TestCase e,
        bool enabled = true) =>
        new(db,
            new(enabled, e.RepositoryId, "gh-org/sandbox"),
            new PostgresGitHubRemoteSandboxDispatchConfirmationJournal(
                db, PostgresGitHubRemoteSandboxPreSendBarrierTests.Options(e),
                new PostgresGitHubRemoteSandboxDispatchIntentJournal(
                    db, PostgresGitHubRemoteSandboxPreSendBarrierTests.Options(e),
                    new PostgresGitHubRemoteSandboxReviewSessionJournal(
                        db, PostgresGitHubRemoteSandboxPreSendBarrierTests.Options(e))),
                new PostgresGitHubRemoteSandboxReviewSessionJournal(
                    db, PostgresGitHubRemoteSandboxPreSendBarrierTests.Options(e))));

    private static Task<GitHubRemoteTypedRecoveryBaselineReceipt> Capture(
        AppDbContext db, PostgresGitHubRemoteDispatchReviewTests.TestCase e,
        GitHubRemoteSandboxDispatchConfirmationReceipt confirmation,
        Guid actor) =>
        Journal(db, e).CaptureAsync(
            confirmation.Id, confirmation.IntentId,
            e.Source.Packet.PreparationId, e.Source.Packet.PlanId,
            e.Source.Packet.ScopeId, actor, e.Source, e.Latest, default);

    private static Task<GitHubRemoteTypedRecoveryBaselineReceipt?> Read(
        AppDbContext db, PostgresGitHubRemoteDispatchReviewTests.TestCase e,
        GitHubRemoteSandboxDispatchConfirmationReceipt confirmation) =>
        Journal(db, e).ReadAfterBarrierAsync(
            confirmation.Id, confirmation.IntentId,
            e.Source.Packet.PreparationId, e.Source.Packet.PlanId,
            e.Source.Packet.ScopeId, default);

    [Fact]
    public async Task F086_captures_before_F082_then_reconstructs_typed_refs_after_restart()
    {
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        GitHubRemoteSandboxDispatchConfirmationReceipt confirmation;
        Guid actor = Guid.NewGuid();
        GitHubRemoteTypedRecoveryBaselineReceipt snapshot;
        await using (var first = fixture.NewContext())
        {
            confirmation = await PostgresGitHubRemoteSandboxPreSendBarrierTests
                .SeedAsync(first, e);
            snapshot = await Capture(first, e, confirmation, actor);
            GitHubRemoteTypedRecoveryBaselineIntegrity.Verify(snapshot);
            Assert.Null(await Read(first, e, confirmation));
            Assert.Equal(e.Latest.Refs, snapshot.Baseline.Refs);
            Assert.Equal(e.Latest.EvidenceSha256,
                snapshot.Baseline.EvidenceSha256);
            Assert.Equal(e.Source.Candidates, snapshot.CreateCandidates);
            Assert.False(snapshot.CanSend);
            Assert.False(snapshot.MayAutomaticallyRetry);
            Assert.False(snapshot.MigrationComplete);
        }
        await using (var markerDb = fixture.NewContext())
        {
            var marker = await PostgresGitHubRemoteSandboxPreSendBarrierTests
                .Journal(markerDb, e).RecordPossibleSendAsync(
                    confirmation.Id, confirmation.IntentId,
                    e.Source.Packet.PreparationId,
                    e.Source.Packet.PlanId, e.Source.Packet.ScopeId,
                    actor, default);
            Assert.Equal(snapshot.CapturedBy, marker.MarkedBy);
        }

        // A fresh process/DbContext reconstructs the exact tag-object
        // types and original branch/tags/OIDs without provider or CLI I/O.
        await using var rebooted = fixture.NewContext();
        var historical = await Read(rebooted, e, confirmation);
        Assert.NotNull(historical);
        Assert.Equal(snapshot.EvidenceSha256, historical.EvidenceSha256);
        Assert.Equal(snapshot.Baseline.Refs, historical.Baseline.Refs);
        Assert.Contains(historical.Baseline.Refs,
            x => x.Name == "refs/tags/v1" && x.ObjectType == "tag");
        Assert.Equal(e.Source.Candidates, historical.CreateCandidates);
        Assert.Equal(
            GitImportRehearsalIntegrity.SealRefs(
                "f044-create-only-candidate-refs-v1", historical.CreateCandidates),
            historical.CandidateSetSha256);
        Assert.False(historical.CanSend);
        var status = await Journal(rebooted, e).InspectScopedAsync(
            confirmation.Id, confirmation.IntentId,
            e.Source.Packet.PreparationId, e.Source.Packet.PlanId,
            e.Source.Packet.ScopeId, default);
        Assert.NotNull(status);
        Assert.True(status.HasPossibleSendBarrier);
        Assert.Equal(2, status.BaselineRefCount);
        Assert.Equal(1, status.CreateCandidateCount);
        Assert.False(status.CanRetry);
        Assert.False(status.MigrationComplete);
        Assert.Null(await Journal(rebooted, e).ReadAfterBarrierAsync(
            confirmation.Id, confirmation.IntentId,
            e.Source.Packet.PreparationId, Guid.NewGuid(),
            e.Source.Packet.ScopeId, default));
    }

    [Fact]
    public async Task F086_six_replicas_cannot_append_different_baselines()
    {
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        GitHubRemoteSandboxDispatchConfirmationReceipt confirmation;
        await using (var setup = fixture.NewContext())
            confirmation = await PostgresGitHubRemoteSandboxPreSendBarrierTests
                .SeedAsync(setup, e);
        var actor = Guid.NewGuid();
        async Task<(Guid? Id, string? Code)> CaptureOnce()
        {
            await using var db = fixture.NewContext();
            try
            {
                var result = await Capture(db, e, confirmation, actor);
                return (result.Id, null);
            }
            catch (GitHubRemoteTypedRecoveryBaselineException ex)
            { return (null, ex.Code); }
        }
        var all = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => CaptureOnce()));
        Assert.Single(all.Where(x => x.Id is not null));
        Assert.All(all.Where(x => x.Id is null),
            x => Assert.Equal("TypedRecoveryBaselineAlreadyCaptured", x.Code));
        await using var check = fixture.NewContext();
        Assert.Single(await check.GitHubRemoteTypedRecoveryBaselines.AsNoTracking()
            .Where(x => x.ConfirmationId == confirmation.Id).ToArrayAsync());
        var winnerId = all.Single(x => x.Id is not null).Id;
        Assert.Single(await check.GitHubRemoteTypedRecoveryBaselineEvents
            .AsNoTracking().Where(x => x.SnapshotId == winnerId)
            .ToArrayAsync());
    }

    [Fact]
    public async Task F086_wrong_actor_ref_scope_or_stale_hazard_fails_closed()
    {
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        await using var db = fixture.NewContext();
        var confirmation = await PostgresGitHubRemoteSandboxPreSendBarrierTests
            .SeedAsync(db, e);
        var cannotSelfReview = await Assert.ThrowsAsync<
            GitHubRemoteTypedRecoveryBaselineException>(() =>
                Capture(db, e, confirmation, confirmation.ConfirmedBy));
        Assert.Equal("TypedRecoveryBaselineAuthorityChanged",
            cannotSelfReview.Code);
        var forged = await Assert.ThrowsAsync<
            GitHubRemoteTypedRecoveryBaselineException>(() =>
                Journal(db, e).CaptureAsync(
                    confirmation.Id, confirmation.IntentId,
                    e.Source.Packet.PreparationId, e.Source.Packet.PlanId,
                    e.Source.Packet.ScopeId, Guid.NewGuid(), e.Source,
                    e.Latest with {
                        Refs = e.Latest.Refs.Select(r => r.Name == "refs/tags/v1"
                            ? r with { ObjectType = "commit" } : r).ToArray()
                    }, default));
        Assert.Equal("TypedRecoveryBaselineInvalid", forged.Code);
        var wrongPlan = await Assert.ThrowsAsync<
            GitHubRemoteTypedRecoveryBaselineException>(() =>
                Journal(db, e).CaptureAsync(
                    confirmation.Id, confirmation.IntentId,
                    e.Source.Packet.PreparationId, Guid.NewGuid(),
                    e.Source.Packet.ScopeId, Guid.NewGuid(), e.Source,
                    e.Latest, default));
        Assert.Equal("TypedRecoveryBaselineInputInvalid", wrongPlan.Code);

        await PostgresGitHubRemoteSandboxPreSendBarrierTests.Journal(db, e)
            .RecordPossibleSendAsync(
                confirmation.Id, confirmation.IntentId,
                e.Source.Packet.PreparationId,
                e.Source.Packet.PlanId, e.Source.Packet.ScopeId,
                Guid.NewGuid(), default);
        var stale = await Assert.ThrowsAsync<
            GitHubRemoteTypedRecoveryBaselineException>(() =>
                Capture(db, e, confirmation, Guid.NewGuid()));
        Assert.Equal("TypedRecoveryBaselineConfirmationStale", stale.Code);
        Assert.Empty(await db.GitHubRemoteTypedRecoveryBaselines.AsNoTracking()
            .Where(x => x.ConfirmationId == confirmation.Id).ToArrayAsync());
    }

    [Fact]
    public async Task F086_sql_mutation_and_delete_of_ref_types_or_audit_are_refused()
    {
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        await using var db = fixture.NewContext();
        var conf = await PostgresGitHubRemoteSandboxPreSendBarrierTests
            .SeedAsync(db, e);
        var actor = Guid.NewGuid();
        var snap = await Capture(db, e, conf, actor);
        await PostgresGitHubRemoteSandboxPreSendBarrierTests.Journal(db, e)
            .RecordPossibleSendAsync(
                conf.Id, conf.IntentId, e.Source.Packet.PreparationId,
                e.Source.Packet.PlanId, e.Source.Packet.ScopeId, actor, default);

        foreach (var sql in new[] {
            $"UPDATE github_remote_typed_recovery_baselines SET can_send=true WHERE id='{snap.Id}'",
            $"UPDATE github_remote_typed_recovery_baselines SET evidence_sha256='{new string('f', 64)}' WHERE id='{snap.Id}'",
            $"UPDATE github_remote_typed_recovery_baselines SET baseline_refs_json='[]' WHERE id='{snap.Id}'",
            $"DELETE FROM github_remote_typed_recovery_baselines WHERE id='{snap.Id}'",
            $"DELETE FROM github_remote_typed_recovery_baseline_events WHERE snapshot_id='{snap.Id}'"
        })
            await Assert.ThrowsAnyAsync<Exception>(() =>
                db.Database.ExecuteSqlRawAsync(sql));
        var proof = await Read(db, e, conf);
        Assert.NotNull(proof);
        Assert.Equal(snap.EvidenceSha256, proof.EvidenceSha256);
    }

    [Fact]
    public async Task F086_disabled_by_default_denies_without_provider_or_db_changes()
    {
        var e = PostgresGitHubRemoteDispatchReviewTests.NewCase();
        await using var db = fixture.NewContext();
        var journal = Journal(db, e, enabled: false);
        var exception = await Assert.ThrowsAsync<
            GitHubRemoteTypedRecoveryBaselineException>(() =>
                journal.CaptureAsync(
                    Guid.NewGuid(), Guid.NewGuid(),
                    e.Source.Packet.PreparationId, e.Source.Packet.PlanId,
                    e.Source.Packet.ScopeId, Guid.NewGuid(),
                    e.Source, e.Latest, default));
        Assert.Equal("TypedRecoveryBaselineDisabled", exception.Code);
    }
}
