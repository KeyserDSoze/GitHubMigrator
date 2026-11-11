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

    // #127: additive append-only assessment history is not a new
    // F-061 generation/worker/token. Its SQL structure refuses direct
    // raw inserts without an actual immutable #124 Isolated parent.
    [Fact]
    public async Task F061v2e_generation_assessment_schema_is_immutable_and_requires_isolation()
    {
        // Repeat the bootstrap to test idempotent upgrade/restart.
        await GitIncrementalLaterCheckpointMirrorGenerationAssessmentSchema
            .InitializeAsync(harness.Host.Services);
        await using var scope = harness.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tables = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*)::bigint AS "Value"
            FROM pg_class
            WHERE relkind = 'r' AND relname IN (
                'git_later_checkpoint_mirror_generation_assessments',
                'git_later_checkpoint_mirror_generation_assessment_events')
            """).SingleAsync();
        var checks = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*)::bigint AS "Value"
            FROM pg_constraint WHERE conname IN (
                'ck_f061v2e_generation_assessment_safe',
                'ck_f061v2e_generation_assessment_event_safe')
            """).SingleAsync();
        var guards = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*)::bigint AS "Value"
            FROM pg_trigger WHERE NOT tgisinternal
              AND tgname IN (
                'trg_f061v2e_generation_assessment',
                'trg_f061v2e_generation_assessment_event')
            """).SingleAsync();
        var noAuthorityDefaults = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*)::bigint AS "Value"
            FROM information_schema.columns
            WHERE table_name =
                'git_later_checkpoint_mirror_generation_assessments'
              AND column_name IN (
                  'durable_readmission_approved', 'can_readmit',
                  'can_materialize', 'can_push', 'can_retry',
                  'can_dispatch', 'migration_complete')
              AND column_default = 'false'
            """).SingleAsync();
        Assert.Equal(2L, tables);
        Assert.Equal(2L, checks);
        Assert.Equal(2L, guards);
        Assert.Equal(7L, noAuthorityDefaults);
        // Even deployments bootstrapped with the earlier audit-only
        // table gain the new provenance fence without rewriting rows.
        var additionalColumns = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*)::bigint AS "Value"
            FROM information_schema.columns
            WHERE table_name =
                'git_later_checkpoint_mirror_generation_assessments'
              AND column_name IN (
                  'recovery_decision_sha256', 'prepared_intent_sha256',
                  'isolation_directory_name', 'object_set_sha256',
                  'object_count', 'source_reviewed_at_utc')
            """).SingleAsync();
        var additionalConstraint = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*)::bigint AS "Value"
            FROM pg_constraint
            WHERE conrelid =
                'git_later_checkpoint_mirror_generation_assessments'::regclass
              AND conname = 'ck_f061v2e_generation_assessment_scope_v2'
              AND contype = 'c'
            """).SingleAsync();
        Assert.Equal(6L, additionalColumns);
        Assert.Equal(1L, additionalConstraint);

        // #128: real PostgreSQL17 correctness and idempotent replay of
        // malformed historical raw-SQL bounds. A 64-digit hex digest
        // MUST match, while 63/65 digits and nonhex characters fail.
        await GitIncrementalLaterCheckpointMirrorRegexConstraintRepairSchema
            .InitializeAsync(harness.Host.Services);
        var shaPredicate = await db.Database.SqlQueryRaw<long>("""
            SELECT CASE WHEN
                repeat('a',64) ~ '^[0-9a-f]{{64}}$'
                AND NOT (repeat('a',63) ~ '^[0-9a-f]{{64}}$')
                AND NOT (repeat('a',65) ~ '^[0-9a-f]{{64}}$')
                AND NOT (repeat('z',64) ~ '^[0-9a-f]{{64}}$')
            THEN 1::bigint ELSE 0::bigint END AS "Value"
            """).SingleAsync();
        Assert.Equal(1L, shaPredicate);
        var malformedBounds = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*)::bigint AS "Value"
            FROM pg_constraint c
            WHERE c.contype = 'c'
              AND c.conrelid IN (
                  'git_later_checkpoint_mirror_leases'::regclass,
                  'git_later_checkpoint_mirror_lease_events'::regclass,
                  'git_later_checkpoint_mirror_verified_evidence'::regclass,
                  'git_later_checkpoint_mirror_recovery_decisions'::regclass,
                  'git_later_checkpoint_mirror_recovery_decision_events'::regclass,
                  'git_later_checkpoint_mirror_isolation_intents'::regclass,
                  'git_later_checkpoint_mirror_isolation_intent_events'::regclass,
                  'git_later_checkpoint_mirror_isolation_receipts'::regclass,
                  'git_later_checkpoint_mirror_isolation_receipt_events'::regclass,
                  'git_later_checkpoint_mirror_generation_assessments'::regclass,
                  'git_later_checkpoint_mirror_generation_assessment_events'::regclass)
              AND position('{{64}}' in pg_get_constraintdef(c.oid)) > 0
            """).SingleAsync();
        Assert.Equal(0L, malformedBounds);

        // A synthetic SHA and a guessed proposed generation cannot
        // directly register a reviewed re-admission against a missing
        // physical isolation. No lease or original journal is altered.
        var fake = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO
                    git_later_checkpoint_mirror_generation_assessments (
                        mirror_id, workpack_id, checkpoint_id,
                        source_snapshot_id, evidence_version,
                        original_worker_id, original_lease_token,
                        original_generation, suggested_next_generation,
                        staging_evidence_sha256,
                        source_full_ref_proof_sha256,
                        source_ref_set_sha256, target_receipt_sha256,
                        bundle_sha256, bundle_bytes,
                        isolation_receipt_sha256,
                        recovery_decision_sha256, prepared_intent_sha256,
                        isolation_directory_name,
                        object_set_sha256, object_count,
                        source_reviewed_at_utc, assessed_at_utc,
                        evidence_sha256)
                VALUES ({Guid.NewGuid()}, {Guid.NewGuid()},
                    {Guid.NewGuid()}, {Guid.NewGuid()}, {1},
                    {Guid.NewGuid()}, {Guid.NewGuid()}, {1L}, {2L},
                    {new string('a',64)}, {new string('b',64)},
                    {new string('c',64)}, {new string('d',64)},
                    {new string('e',64)}, {123L}, {new string('f',64)},
                    {new string('a',64)}, {new string('b',64)},
                    {Guid.NewGuid().ToString("N") + "." +
                        new string('a',64) + ".quarantine.git"},
                    {new string('c',64)}, {10},
                    {DateTimeOffset.UtcNow},
                    {DateTimeOffset.UtcNow}, {new string('a',64)})
                """));
        Assert.Contains(fake.SqlState, new[] {
            Npgsql.PostgresErrorCodes.RaiseException,
            Npgsql.PostgresErrorCodes.ForeignKeyViolation
        });
        Assert.Equal(0L, await db.Database.SqlQueryRaw<long>("""
            SELECT count(*)::bigint AS "Value"
            FROM git_later_checkpoint_mirror_generation_assessments
            """).SingleAsync());
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
        // #124: when upgrading an existing PREPARED-only database,
        // not just on a fresh CREATE TABLE, all new post-move
        // provenance columns and the dedicated CHECK must exist.
        var receiptColumns = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*)::bigint AS "Value"
            FROM information_schema.columns
            WHERE table_name =
                'git_later_checkpoint_mirror_isolation_receipts'
                AND column_name IN (
                    'original_worker_id', 'original_lease_token',
                    'original_generation', 'bundle_sha256',
                    'bundle_bytes', 'reconciled_existing_quarantine')
                AND is_nullable = 'NO'
            """).SingleAsync();
        var upgradedConstraint = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*)::bigint AS "Value"
            FROM pg_constraint
            WHERE conrelid =
                'git_later_checkpoint_mirror_isolation_receipts'::regclass
                AND conname = 'ck_f061v2_isolation_receipt_provenance_v2'
                AND contype = 'c'
            """).SingleAsync();
        Assert.Equal(6L, receiptColumns);
        Assert.Equal(1L, upgradedConstraint);
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
        // BEFORE INSERT trigger validates the immutable reviewer/lease
        // boundary before PostgreSQL evaluates its FK. Either way this
        // forged intent is refused, and the journal remains empty.
        Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException,
            invalid.SqlState);
        Assert.Equal(0L, await db.Database.SqlQuery<long>($"""
            SELECT count(*)::bigint AS "Value"
            FROM git_later_checkpoint_mirror_isolation_intents
            WHERE mirror_id={mirror}
            """).SingleAsync());
    }

    [Fact]
    public async Task F061v2_isolation_intent_writer_is_default_OFF_without_file_or_provider_actions()
    {
        var before = harness.Calls.Count;
        await using var scope = harness.Host.Services.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<
            PostgresGitIncrementalLaterCheckpointMirrorIsolationIntentJournal>();
        var error = await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
            () => writer.PrepareAsync(Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                "repo-1", new string('a', 64), Guid.NewGuid(), default));
        Assert.Equal("LaterCheckpointMirrorIsolationIntentDisabled", error.Code);
        Assert.Equal(before, harness.Calls.Count);
    }

    [Fact]
    public async Task F061v2_post_crash_source_history_reader_is_default_OFF_and_read_only()
    {
        var before = harness.Calls.Count;
        await using var scope = harness.Host.Services.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<
            PostgresGitIncrementalLaterCheckpointMirrorPreparedReconciliationReader>();
        var denied = await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
            () => reader.ReadAsync(Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                "repo-1", new string('a', 64), Guid.NewGuid(), default));
        Assert.Equal("LaterCheckpointMirrorIsolationReconciliationDisabled",
            denied.Code);
        Assert.Equal(before, harness.Calls.Count);
    }

    [Fact]
    public async Task F061v2_physical_isolation_coordinator_is_default_OFF_without_database_or_Git()
    {
        var before = harness.Calls.Count;
        await using var scope = harness.Host.Services.CreateAsyncScope();
        var coordinator = scope.ServiceProvider.GetRequiredService<
            PostgresGitIncrementalLaterCheckpointMirrorIsolationCoordinator>();
        var failure = await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
            () => coordinator.RunAsync(Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "repo-1",
                new string('a', 64), Guid.NewGuid(), default));
        Assert.Equal("LaterCheckpointMirrorIsolationCoordinatorDisabled",
            failure.Code);
        Assert.Equal(before, harness.Calls.Count);
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

    [Fact]
    public async Task F061v2e_second_review_candidate_is_superadmin_only_default_off_GET_only()
    {
        // Uses REAL JWT middleware and the production API host, without
        // enabling a filesystem root, SQL writer, remote transport, or
        // F-061 lease worker. The candidate is never an authorization.
        var path = "/api/incremental-preparations/" + Guid.NewGuid() +
            "/operation-plans/" + Guid.NewGuid() +
            "/source-sync-checkpoints/" + Guid.NewGuid() +
            "/mirror-recovery/second-review/candidate" +
            "?targetObservationId=" + Guid.NewGuid() +
            "&workpackId=" + Guid.NewGuid() +
            "&originalBundleActorId=" + Guid.NewGuid() +
            "&sourceRepositoryId=repo-1" +
            "&expectedTargetReceiptSha256=" + new string('a', 64);
        var admin = await harness.LoginAsync(
            harness.AdminName, ApiHarness.AdminPassword);
        var user = await harness.LoginAsync(
            harness.UserName, ApiHarness.UserPassword);
        var superadmin = await harness.LoginAsync(
            harness.SuperadminName, ApiHarness.SuperadminPassword);
        var providerCalls = harness.Calls.Count;

        foreach (var (bearer, expected) in new[] {
            ((string?)null, HttpStatusCode.Unauthorized),
            (user, HttpStatusCode.Forbidden),
            (admin, HttpStatusCode.Forbidden),
            (superadmin, HttpStatusCode.Conflict)
        })
        {
            using var reply = await Send(harness.Client, HttpMethod.Get,
                path, bearer);
            Assert.Equal(expected, reply.StatusCode);
            if (bearer == superadmin)
            {
                using var result = JsonDocument.Parse(
                    await reply.Content.ReadAsStringAsync());
                Assert.Equal("LaterCheckpointMirrorPostIsolationInspectorDisabled",
                    result.RootElement.GetProperty("error").GetString());
                Assert.DoesNotContain(ApiHarness.SourceToken,
                    result.RootElement.GetRawText());
                Assert.DoesNotContain(ApiHarness.TargetToken,
                    result.RootElement.GetRawText());
            }
        }
        using (var post = await Send(harness.Client, HttpMethod.Post,
            path, superadmin))
            Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        Assert.Equal(providerCalls, harness.Calls.Count);

        await using var scope = harness.Host.Services.CreateAsyncScope();
        var diagnostic = scope.ServiceProvider.GetRequiredService<
            PostgresGitIncrementalLaterCheckpointMirrorPostIsolationInspector>();
        var denied = await Assert.ThrowsAsync<
            GitSourceSnapshotMirrorException>(() =>
            diagnostic.ReadAsync(Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                "repo-1", new string('a', 64), Guid.NewGuid(), default));
        Assert.Equal("LaterCheckpointMirrorPostIsolationInspectorDisabled",
            denied.Code);
    }

    [Fact]
    public async Task F061v2e_second_review_preflight_requires_real_jwt_and_never_writes()
    {
        var route = "/api/incremental-preparations/" + Guid.NewGuid() +
            "/operation-plans/" + Guid.NewGuid() +
            "/source-sync-checkpoints/" + Guid.NewGuid() +
            "/mirror-recovery/second-review/preflight";
        var valid = new {
            targetObservationId = Guid.NewGuid(),
            workpackId = Guid.NewGuid(),
            originalBundleActorId = Guid.NewGuid(),
            sourceRepositoryId = "repo-1",
            expectedTargetReceiptSha256 = new string('a', 64),
            expectedAssessmentEvidenceSha256 = new string('b', 64),
            expectedReviewEvidenceSha256 = new string('c', 64),
            decision = "ApprovedForIndependentReview",
            reviewedAtUtc = DateTimeOffset.UtcNow,
            decisionNonce = Guid.NewGuid()
        };
        var admin = await harness.LoginAsync(
            harness.AdminName, ApiHarness.AdminPassword);
        var user = await harness.LoginAsync(
            harness.UserName, ApiHarness.UserPassword);
        var superadmin = await harness.LoginAsync(
            harness.SuperadminName, ApiHarness.SuperadminPassword);
        var outbound = harness.Calls.Count;
        foreach (var (bearer, expected) in new[] {
            ((string?)null, HttpStatusCode.Unauthorized),
            (user, HttpStatusCode.Forbidden),
            (admin, HttpStatusCode.Forbidden),
            (superadmin, HttpStatusCode.Conflict)
        })
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, route)
            {
                Content = System.Net.Http.Json.JsonContent.Create(valid)
            };
            if (bearer is not null)
                message.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", bearer);
            using var reply = await harness.Client.SendAsync(message);
            Assert.Equal(expected, reply.StatusCode);
            if (bearer == superadmin)
                Assert.Contains(
                    "LaterCheckpointMirrorPostIsolationInspectorDisabled",
                    await reply.Content.ReadAsStringAsync());
        }
        Assert.Equal(outbound, harness.Calls.Count);
    }

    [Fact]
    public async Task F061v2e_second_reviewer_token_checks_current_account_stamp_and_role()
    {
        // Isolated host: rotating security stamp or removing the role
        // must revoke the candidate privilege immediately, not after
        // the ordinary signed JWT's 30-minute expiry. No filesystem
        // or GitHub provider access is enabled in this test.
        var separate = new ApiHarness();
        try
        {
            await separate.InitializeAsync();
            var url = "/api/incremental-preparations/" + Guid.NewGuid() +
                "/operation-plans/" + Guid.NewGuid() +
                "/source-sync-checkpoints/" + Guid.NewGuid() +
                "/mirror-recovery/second-review/candidate" +
                "?targetObservationId=" + Guid.NewGuid() +
                "&workpackId=" + Guid.NewGuid() +
                "&originalBundleActorId=" + Guid.NewGuid() +
                "&sourceRepositoryId=repo-1" +
                "&expectedTargetReceiptSha256=" + new string('a', 64);
            var token = await separate.LoginAsync(
                separate.SuperadminName, ApiHarness.SuperadminPassword);
            var before = separate.Calls.Count;
            using (var initial = await Send(separate.Client,
                HttpMethod.Get, url, token))
                Assert.Equal(HttpStatusCode.Conflict, initial.StatusCode);

            await using var scope = separate.Host.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<
                Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
            var account = await users.FindByNameAsync(separate.SuperadminName)
                ?? throw new InvalidOperationException("superadmin missing");
            Assert.True((await users.UpdateSecurityStampAsync(account)).Succeeded);
            using (var revoked = await Send(separate.Client,
                HttpMethod.Get, url, token))
                Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);

            var renewed = await separate.LoginAsync(
                separate.SuperadminName, ApiHarness.SuperadminPassword,
                forceFresh: true);
            Assert.NotEqual(token, renewed);
            using (var restored = await Send(separate.Client,
                HttpMethod.Get, url, renewed))
                Assert.Equal(HttpStatusCode.Conflict, restored.StatusCode);
            Assert.True((await users.RemoveFromRoleAsync(
                account, "superadmin")).Succeeded);
            using (var noRole = await Send(separate.Client,
                HttpMethod.Get, url, renewed))
                Assert.Equal(HttpStatusCode.Unauthorized, noRole.StatusCode);
            Assert.Equal(before, separate.Calls.Count);
        }
        finally
        {
            await separate.DisposeAsync();
        }
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
