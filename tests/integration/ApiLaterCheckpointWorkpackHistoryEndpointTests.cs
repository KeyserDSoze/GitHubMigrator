using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// #111: the historical v2 workpack endpoint is GET-only, superadmin-only,
// and OFF by default. No remote provider GET, sender or bundle worker.
public sealed class ApiLaterCheckpointWorkpackHistoryEndpointTests(
    ApiHarness harness) : IClassFixture<ApiHarness>
{
    [Fact]
    public async Task F097c4c2_default_off_and_JWT_gate_never_contact_provider()
    {
        var reviewer = await harness.LoginAsync(
            harness.SuperadminName, ApiHarness.SuperadminPassword);
        var admin = await harness.LoginAsync(
            harness.AdminName, ApiHarness.AdminPassword);
        var user = await harness.LoginAsync(
            harness.UserName, ApiHarness.UserPassword);
        var url = Root();
        var calls = harness.Calls.Count;

        foreach (var (token, expected) in new[] {
            ((string?)null, HttpStatusCode.Unauthorized),
            (user, HttpStatusCode.Forbidden),
            (admin, HttpStatusCode.Forbidden),
            (reviewer, HttpStatusCode.Conflict)
        })
        {
            using var reply = await Send(harness.Client, HttpMethod.Get,
                url, token);
            Assert.Equal(expected, reply.StatusCode);
            if (token == reviewer)
            {
                using var data = JsonDocument.Parse(
                    await reply.Content.ReadAsStringAsync());
                Assert.Equal("LaterCheckpointHistoryDisabled",
                    data.RootElement.GetProperty("error").GetString());
            }
        }
        using (var post = await Send(harness.Client, HttpMethod.Post,
            url, reviewer))
            Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        Assert.Equal(calls, harness.Calls.Count);
    }

    [Fact]
    public async Task F035v2_local_bundle_rehearsal_is_independently_disabled_by_default()
    {
        var before = harness.Calls.Count;
        await using var scope = harness.Host.Services.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<
            GitIncrementalLaterCheckpointLocalBundleAcquisition>();
        var denied = await Assert.ThrowsAsync<GitSourceBundleException>(() =>
            runner.RehearseAsync(Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                "repo-1", new string('a', 64), default));
        Assert.Equal("LaterCheckpointLocalBundleDisabled", denied.Code);
        Assert.Equal(before, harness.Calls.Count);
    }

    [Fact]
    public async Task F060v2_mirror_staging_read_is_independently_disabled_by_default()
    {
        var before = harness.Calls.Count;
        await using var scope = harness.Host.Services.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<
            PostgresGitIncrementalLaterCheckpointMirrorStagingPreflight>();
        var denied = await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(() =>
            reader.ReadAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), "repo-1",
                new string('a', 64), Guid.NewGuid(), default));
        Assert.Equal("LaterCheckpointMirrorStagingDisabled", denied.Code);
        Assert.Equal(before, harness.Calls.Count);
    }

    [Fact]
    public async Task F061v2_schema_is_isolated_from_baseline_and_requires_immutable_events()
    {
        await using var scope = harness.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var f035V2Fk = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*)::bigint AS "Value" FROM pg_constraint
            WHERE conrelid='git_later_checkpoint_mirror_leases'::regclass
              AND confrelid='git_later_checkpoint_bundle_evidence'::regclass
              AND contype='f'
            """).SingleAsync();
        var legacyFk = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*)::bigint AS "Value" FROM pg_constraint
            WHERE conrelid='git_later_checkpoint_mirror_leases'::regclass
              AND confrelid='git_source_mirror_lifecycles'::regclass
              AND contype='f'
            """).SingleAsync();
        Assert.Equal(1L, f035V2Fk);
        Assert.Equal(0L, legacyFk);

        var safety = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*)::bigint AS "Value" FROM pg_constraint
            WHERE conrelid='git_later_checkpoint_mirror_leases'::regclass
              AND conname='ck_later_f061v2_mirror_safety'
            """).SingleAsync();
        var eventSafety = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*)::bigint AS "Value" FROM pg_constraint
            WHERE conrelid='git_later_checkpoint_mirror_lease_events'::regclass
              AND conname='ck_later_f061v2_event_safety'
            """).SingleAsync();
        var guards = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*)::bigint AS "Value" FROM pg_trigger
            WHERE NOT tgisinternal AND (
                (tgrelid='git_later_checkpoint_mirror_leases'::regclass
                 AND tgname='trg_later_f061v2_lease_guard')
                OR
                (tgrelid='git_later_checkpoint_mirror_lease_events'::regclass
                 AND tgname='trg_later_f061v2_event_immutable')
            )
            """).SingleAsync();
        Assert.Equal(1L, safety);
        Assert.Equal(1L, eventSafety);
        Assert.Equal(2L, guards);
    }

    [Fact]
    public async Task F061v2_recovery_review_is_independently_default_OFF_and_requires_superadmin_JWT()
    {
        var before = harness.Calls.Count;
        await using (var scope = harness.Host.Services.CreateAsyncScope())
        {
            var journal = scope.ServiceProvider.GetRequiredService<
                PostgresGitIncrementalLaterCheckpointMirrorRecoveryReviewJournal>();
            var denied = await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                () => journal.RecordAsync(Guid.NewGuid(), Guid.NewGuid(),
                    Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                    "repo-1", new string('a', 64), Guid.NewGuid(),
                    Guid.NewGuid(), "Rejected", new string('b', 64),
                    default));
            Assert.Equal("LaterCheckpointMirrorRecoveryReviewDisabled",
                denied.Code);
        }

        var path = "/api/incremental-preparations/" + Guid.NewGuid() +
            "/operation-plans/" + Guid.NewGuid() +
            "/source-sync-checkpoints/" + Guid.NewGuid() +
            "/mirror-recovery";
        var query = path + "/candidate?sourceRepositoryId=repo-1" +
            "&targetObservationId=" + Guid.NewGuid() +
            "&workpackId=" + Guid.NewGuid() +
            "&originalBundleActorId=" + Guid.NewGuid() +
            "&expectedTargetReceiptSha256=" + new string('a',64);
        var admin = await harness.LoginAsync(
            harness.AdminName, ApiHarness.AdminPassword);
        var user = await harness.LoginAsync(
            harness.UserName, ApiHarness.UserPassword);
        var superadmin = await harness.LoginAsync(
            harness.SuperadminName, ApiHarness.SuperadminPassword);
        foreach (var (bearer, expected) in new[] {
            ((string?)null, HttpStatusCode.Unauthorized),
            (user, HttpStatusCode.Forbidden),
            (admin, HttpStatusCode.Forbidden),
            (superadmin, HttpStatusCode.Conflict)
        })
        {
            using var reply = await Send(harness.Client, HttpMethod.Get,
                query, bearer);
            Assert.Equal(expected, reply.StatusCode);
            if (bearer == superadmin)
                Assert.Contains("LaterCheckpointMirrorRecoveryInspectionDisabled",
                    await reply.Content.ReadAsStringAsync());
        }
        using (var postUnauthorized = await Send(harness.Client,
            HttpMethod.Post, path + "/decision", null))
            Assert.Equal(HttpStatusCode.Unauthorized,
                postUnauthorized.StatusCode);
        Assert.Equal(before, harness.Calls.Count);
    }

    [Fact]
    public async Task F061v2_recovery_review_schema_is_immutable_source_only_and_not_legacy_F061()
    {
        await using var scope = harness.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var fk = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*)::bigint AS "Value" FROM pg_constraint
            WHERE conrelid =
                'git_later_checkpoint_mirror_recovery_decisions'::regclass
                AND confrelid =
                'git_later_checkpoint_mirror_leases'::regclass
                AND contype = 'f'
            """).SingleAsync();
        var old = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*)::bigint AS "Value" FROM pg_constraint
            WHERE conrelid =
                'git_later_checkpoint_mirror_recovery_decisions'::regclass
                AND confrelid =
                'git_source_mirror_lifecycles'::regclass
                AND contype = 'f'
            """).SingleAsync();
        var check = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*)::bigint AS "Value" FROM pg_constraint
            WHERE conrelid =
                'git_later_checkpoint_mirror_recovery_decisions'::regclass
                AND conname='ck_f061v2_recovery_review_safe'
            """).SingleAsync();
        var immutable = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*)::bigint AS "Value" FROM pg_trigger
            WHERE NOT tgisinternal AND (
                (tgrelid =
                   'git_later_checkpoint_mirror_recovery_decisions'::regclass
                 AND tgname='trg_f061v2_recovery_decision_guard')
                OR (tgrelid =
                   'git_later_checkpoint_mirror_recovery_decision_events'::regclass
                 AND tgname='trg_f061v2_recovery_decision_event'))
            """).SingleAsync();
        Assert.Equal(1L, fk);
        Assert.Equal(0L, old);
        Assert.Equal(1L, check);
        Assert.Equal(2L, immutable);
    }

    [Fact]
    public async Task F061v2_orphan_isolation_journal_has_two_immutable_non_authorizing_phases()
    {
        await using var scope = harness.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Installing schema alone grants neither permission to move a
        // mirror nor permission to adopt a RecoveryRequired lease.
        var tables = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*)::bigint AS "Value"
            FROM pg_class WHERE relkind = 'r' AND relname IN (
                'git_later_checkpoint_mirror_isolation_intents',
                'git_later_checkpoint_mirror_isolation_intent_events',
                'git_later_checkpoint_mirror_isolation_receipts',
                'git_later_checkpoint_mirror_isolation_receipt_events')
            """).SingleAsync();
        var checks = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*)::bigint AS "Value"
            FROM pg_constraint WHERE conname IN (
                'ck_f061v2_isolation_intent_safe',
                'ck_f061v2_isolation_intent_event_safe',
                'ck_f061v2_isolation_receipt_safe',
                'ck_f061v2_isolation_receipt_event_safe')
            """).SingleAsync();
        var guards = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*)::bigint AS "Value"
            FROM pg_trigger WHERE NOT tgisinternal
              AND tgname IN (
                'trg_f061v2_isolation_intent',
                'trg_f061v2_isolation_intent_event',
                'trg_f061v2_isolation_receipt',
                'trg_f061v2_isolation_receipt_event')
            """).SingleAsync();
        var reviewFk = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*)::bigint AS "Value"
            FROM pg_constraint
            WHERE conrelid =
                'git_later_checkpoint_mirror_isolation_intents'::regclass
              AND confrelid =
                'git_later_checkpoint_mirror_recovery_decisions'::regclass
              AND contype='f'
            """).SingleAsync();
        var legacyFk = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*)::bigint AS "Value"
            FROM pg_constraint
            WHERE conrelid =
                'git_later_checkpoint_mirror_isolation_intents'::regclass
              AND confrelid='git_source_mirror_lifecycles'::regclass
              AND contype='f'
            """).SingleAsync();

        Assert.Equal(4L, tables);
        Assert.Equal(4L, checks);
        Assert.Equal(4L, guards);
        Assert.Equal(1L, reviewFk);
        Assert.Equal(0L, legacyFk);
        var flags = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*)::bigint AS "Value"
            FROM information_schema.columns
            WHERE table_name IN (
                'git_later_checkpoint_mirror_isolation_intents',
                'git_later_checkpoint_mirror_isolation_receipts')
              AND column_name IN (
                'can_readmit', 'can_materialize', 'can_push',
                'can_retry', 'can_dispatch', 'migration_complete')
              AND column_default = 'false'
            """).SingleAsync();
        Assert.Equal(12L, flags);
        // A fabricated decision cannot create an intent; the signed
        // reviewer decision is an actual FK, never a caller's SHA string.
        var mirror = Guid.NewGuid();
        var worker = Guid.NewGuid();
        var token = Guid.NewGuid();
        var workpack = Guid.NewGuid();
        var sha = new string('a', 64);
        var quarantineName = mirror.ToString("N") + "." + sha +
            ".quarantine.git";
        var invalid = await Assert.ThrowsAsync<Npgsql.PostgresException>(
            () => db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO git_later_checkpoint_mirror_isolation_intents (
                    mirror_id,workpack_id,checkpoint_id,source_snapshot_id,
                    original_worker_id,original_lease_token,
                    original_generation,recovery_decision_sha256,
                    inspection_sha256,lease_event_history_sha256,
                    staging_evidence_sha256,object_set_sha256,
                    object_count,isolation_directory_name,
                    prepared_at_utc,intent_sha256)
                VALUES (
                    {mirror},{workpack},{Guid.NewGuid()},{Guid.NewGuid()},
                    {worker},{token},{1L},{sha},{sha},{sha},{sha},{sha},
                    {4},{quarantineName},{DateTimeOffset.UtcNow},{sha})
                """));
        Assert.Equal(Npgsql.PostgresErrorCodes.ForeignKeyViolation,
            invalid.SqlState);
        Assert.Equal(0L, await db.Database.SqlQuery<long>($"""
            SELECT count(*)::bigint AS "Value"
            FROM git_later_checkpoint_mirror_isolation_intents
            WHERE mirror_id={mirror}
            """).SingleAsync());
    }

    [Fact]
    public async Task F061v2_recovery_inspection_is_independently_default_OFF_and_no_provider_calls()
    {
        var before = harness.Calls.Count;
        await using var scope = harness.Host.Services.CreateAsyncScope();
        var reviewer = scope.ServiceProvider.GetRequiredService<
            PostgresGitIncrementalLaterCheckpointMirrorRecoveryInspector>();
        var rejected = await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
            () => reviewer.ReadAsync(Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                "repo-1", new string('a', 64), Guid.NewGuid(), default));
        Assert.Equal("LaterCheckpointMirrorRecoveryInspectionDisabled",
            rejected.Code);
        Assert.Equal(before, harness.Calls.Count);
    }

    [Fact]
    public async Task F061v2_local_import_worker_is_default_OFF_with_no_DB_or_provider_work()
    {
        var originalCalls = harness.Calls.Count;
        await using var scope = harness.Host.Services.CreateAsyncScope();
        var worker = scope.ServiceProvider.GetRequiredService<
            GitIncrementalLaterCheckpointMirrorImportWorker>();
        var refused = await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
            () => worker.RunAsync(Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                "repo-1", new string('a', 64), Guid.NewGuid(),
                Guid.NewGuid(), default));
        Assert.Equal("LaterCheckpointMirrorImportWorkerDisabled", refused.Code);
        Assert.Equal(originalCalls, harness.Calls.Count);
    }

    [Fact]
    public async Task F061v2_physical_Verified_writer_is_default_OFF_with_zero_provider_calls()
    {
        var before = harness.Calls.Count;
        await using var scope = harness.Host.Services.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredService<
            PostgresGitIncrementalLaterCheckpointMirrorVerifiedJournal>();
        var rejected = await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
            () => journal.CompleteAsync(null!, Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), "repo-1",
                new string('a', 64), default));
        Assert.Equal("LaterCheckpointMirrorVerifiedDisabled", rejected.Code);
        Assert.Equal(before, harness.Calls.Count);
    }

    [Fact]
    public async Task F061v2_Verified_schema_is_immutable_and_separate_from_legacy_F061()
    {
        await using var scope = harness.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var v2 = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*)::bigint AS "Value" FROM pg_constraint
            WHERE conrelid =
                'git_later_checkpoint_mirror_verified_evidence'::regclass
                AND confrelid =
                'git_later_checkpoint_mirror_leases'::regclass
                AND contype = 'f'
            """).SingleAsync();
        var legacy = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*)::bigint AS "Value" FROM pg_constraint
            WHERE conrelid =
                'git_later_checkpoint_mirror_verified_evidence'::regclass
                AND confrelid =
                'git_source_mirror_lifecycles'::regclass
                AND contype = 'f'
            """).SingleAsync();
        var checks = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*)::bigint AS "Value" FROM pg_constraint
            WHERE conrelid =
                'git_later_checkpoint_mirror_verified_evidence'::regclass
                AND conname = 'ck_later_f061v2_verified_safe'
            """).SingleAsync();
        var guard = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*)::bigint AS "Value" FROM pg_trigger
            WHERE tgrelid =
                'git_later_checkpoint_mirror_verified_evidence'::regclass
                AND tgname = 'trg_later_f061v2_verified_guard'
                AND NOT tgisinternal
            """).SingleAsync();
        Assert.Equal(1, v2);
        Assert.Equal(0, legacy);
        Assert.Equal(1, checks);
        Assert.Equal(1, guard);
    }

    [Fact]
    public async Task F061v2_claim_is_independently_disabled_by_default_without_provider_io()
    {
        var prior = harness.Calls.Count;
        await using var scope = harness.Host.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<
            PostgresGitIncrementalLaterCheckpointMirrorLeaseStore>();
        var rejected = await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
            () => store.ClaimAsync(Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                "repo-1", new string('a', 64), Guid.NewGuid(),
                Guid.NewGuid(), TimeSpan.FromMinutes(2), default));
        Assert.Equal("LaterCheckpointMirrorLeaseDisabled", rejected.Code);
        Assert.Equal(prior, harness.Calls.Count);
    }

    [Fact]
    public async Task F061v2_locked_admission_read_is_independently_disabled_by_default()
    {
        var before = harness.Calls.Count;
        await using var scope = harness.Host.Services.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<
            PostgresGitIncrementalLaterCheckpointMirrorAdmissionPreflight>();
        var denied = await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
            () => reader.ReadAsync(Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                "repo-1", new string('a', 64), Guid.NewGuid(), default));
        Assert.Equal("LaterCheckpointMirrorAdmissionDisabled", denied.Code);
        Assert.Equal(before, harness.Calls.Count);
    }

    [Fact]
    public async Task F035v2_durable_file_writer_is_independently_disabled_by_default()
    {
        var before = harness.Calls.Count;
        await using var scope = harness.Host.Services.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<
            PostgresGitIncrementalLaterCheckpointDurableBundleJournal>();
        var denied = await Assert.ThrowsAsync<GitSourceBundleException>(() =>
            writer.CaptureAsync(Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                "repo-1", new string('a', 64),
                Guid.NewGuid(), default));
        Assert.Equal("LaterCheckpointDurableBundleDisabled", denied.Code);
        Assert.Equal(before, harness.Calls.Count);
    }

    [Fact]
    public async Task F035v2_lease_coordinator_is_independently_disabled_by_default()
    {
        var calls = harness.Calls.Count;
        await using var scope = harness.Host.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<
            PostgresGitIncrementalLaterCheckpointBundleLeaseStore>();
        var denied = await Assert.ThrowsAsync<GitSourceBundleException>(() =>
            store.ClaimAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), "repo-1",
                new string('a', 64), Guid.NewGuid(), TimeSpan.FromMinutes(2),
                default));
        Assert.Equal("LaterCheckpointBundleLeaseDisabled", denied.Code);
        Assert.Equal(calls, harness.Calls.Count);
    }

    [Fact]
    public async Task F035v2_source_bundle_read_is_independently_disabled_by_default()
    {
        var outboundBefore = harness.Calls.Count;
        await using var scope = harness.Host.Services.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<
            PostgresGitIncrementalLaterCheckpointBundleReadPreflight>();
        var denied = await Assert.ThrowsAsync<GitSourceBundleException>(() =>
            reader.ReadAsync(Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                "repo-1", new string('a', 64), default));
        Assert.Equal("LaterCheckpointBundleReadDisabled", denied.Code);
        Assert.Equal(outboundBefore, harness.Calls.Count);
    }

    [Fact]
    public async Task F097c4c2_enabled_read_refuses_invalid_scope_and_missing_history()
    {
        var enabled = new ApiHarness { EnableLaterCheckpointHistory = true };
        try
        {
            await enabled.InitializeAsync();
            var token = await enabled.LoginAsync(
                enabled.SuperadminName, ApiHarness.SuperadminPassword);
            var calls = enabled.Calls.Count;
            using (var invalid = await Send(enabled.Client, HttpMethod.Get,
                Root().Replace(new string('a', 64), "bad"), token))
            {
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                Assert.Contains("LaterCheckpointHistoryInvalidRequest",
                    await invalid.Content.ReadAsStringAsync());
            }
            using (var missing = await Send(enabled.Client, HttpMethod.Get,
                Root(), token))
            {
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
                Assert.Contains("LaterCheckpointHistoryNotFound",
                    await missing.Content.ReadAsStringAsync());
            }
            Assert.Equal(calls, enabled.Calls.Count);
        }
        finally { await enabled.DisposeAsync(); }
    }

    private static string Root() =>
        "/api/incremental-preparations/" + Guid.NewGuid() +
        "/operation-plans/" + Guid.NewGuid() +
        "/source-sync-checkpoints/" + Guid.NewGuid() +
        "/later-workpacks/" + Guid.NewGuid() +
        "/history?sourceRepositoryId=repo-1&targetObservationId=" +
        Guid.NewGuid() + "&expectedTargetReceiptSha256=" + new string('a', 64);

    private static async Task<HttpResponseMessage> Send(
        HttpClient client, HttpMethod method, string url, string? token)
    {
        using var request = new HttpRequestMessage(method, url);
        if (token is not null)
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}
