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
              -- EF composite formatting applies even to SQL
              -- comments. Four source braces yield the malformed
              -- two-brace pattern, not the healthy regex string.
              AND position('{{{{64}}}}' in pg_get_constraintdef(c.oid)) > 0
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
    public async Task F061v2e_readonly_review_preflight_uses_real_postgres_clock()
    {
        // #130: a real PostgreSQL server instant must authorize only
        // a *read-only candidate*, not an actual decision, event or
        // session consumption. This does not fake DB time with the
        // API's TimeProvider or disable a single mutation trigger.
        await using var scope = harness.Host.Services.CreateAsyncScope();
        var readiness = scope.ServiceProvider.GetRequiredService<
            PostgresGitIncrementalLaterCheckpointMirrorSecondReviewSessionReadiness>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var issued = DateTimeOffset.FromUnixTimeSeconds(
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var session = Guid.NewGuid();
        var nonce = Guid.NewGuid();
        var mirror = Guid.NewGuid();
        var observed = await readiness.RequireUnusedAsync(
            session, nonce, issued, issued, Guid.Empty, default);
        Assert.Equal(TimeSpan.Zero, observed.Offset);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(
            observed.ToUnixTimeMilliseconds()), observed);
        Assert.True(observed >= issued && observed < issued.AddMinutes(5));
        // Second pass is scoped to the trusted mirror; an unconsumed
        // session remains available for read-only preview only.
        var second = await readiness.RequireUnusedAsync(
            session, nonce, issued, issued, mirror, default);
        Assert.True(second >= observed);

        async Task Reject(Guid s, Guid n, DateTimeOffset issuedAt,
            DateTimeOffset reviewedAt, string code)
        {
            var ex = await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                () => readiness.RequireUnusedAsync(
                    s, n, issuedAt, reviewedAt, mirror, default));
            Assert.Equal(code, ex.Code);
        }
        await Reject(session, session, issued, issued,
            "LaterCheckpointMirrorSecondReviewPreflightInvalid");
        await Reject(session, nonce, issued.AddMinutes(-6),
            issued.AddMinutes(-6),
            "LaterCheckpointMirrorSecondReviewPreflightExpired");
        await Reject(session, nonce, issued, issued.AddMinutes(1),
            "LaterCheckpointMirrorSecondReviewPreflightExpired");
        await Reject(session, nonce, issued.AddTicks(1), issued,
            "LaterCheckpointMirrorSecondReviewPreflightInvalid");
        await Reject(session, nonce, issued, issued.AddTicks(1),
            "LaterCheckpointMirrorSecondReviewPreflightInvalid");
        foreach (var table in new[] {
            "git_later_checkpoint_mirror_second_reviews",
            "git_later_checkpoint_mirror_second_review_events",
            "git_later_checkpoint_mirror_second_review_session_consumptions"
        })
        {
            // Table names are fixed test literals, never request input.
            Assert.Equal(0L, await db.Database.SqlQueryRaw<long>(
                "SELECT count(*)::bigint AS \"Value\" FROM " + table)
                .SingleAsync());
        }
    }

    [Fact]
    public async Task F061v2e_real_postgres_session_checks_reject_noncanonical_evidence()
    {
        // #130: exercise PostgreSQL's INSTALLED CHECK expressions, not
        // a C# reimplementation or a string-only pg_constraint search.
        // LIKE INCLUDING CONSTRAINTS copies the actual CHECKs and
        // NOT NULL; INCLUDING DEFAULTS keeps immutable false flags.
        // INCLUDING INDEXES copies the actual global one-use session
        // PRIMARY KEY and the UNIQUE nonce / mirror fences. A TEMP
        // table does NOT copy parent FKs or mutation triggers; the
        // real journal remains DENY-ALL and is tested separately.
        // The production session table must remain DENY-ALL.
        await using var scope = harness.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.OpenConnectionAsync();
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TEMP TABLE f061_second_review_session_probe (
                    LIKE git_later_checkpoint_mirror_second_review_session_consumptions
                        INCLUDING DEFAULTS INCLUDING CONSTRAINTS
                        INCLUDING INDEXES
                ) ON COMMIT DROP
                """);

            var issued = new DateTimeOffset(
                2026, 11, 11, 18, 0, 0, TimeSpan.Zero);
            var reviewed = issued.AddSeconds(30);
            var consumed = issued.AddSeconds(31);
            var expires = issued.AddMinutes(5);
            var reviewer = Guid.NewGuid();
            var decision =
                GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity
                    .ApprovedForIndependentReview;
            var sha = new string('a', 64);
            var intentSha = new string('b', 64);

            Task<int> Insert(
                Guid session, Guid mirror, Guid nonce,
                string reviewSha, string sessionSha,
                DateTimeOffset issuedAt, DateTimeOffset reviewedAt,
                DateTimeOffset consumedAt, DateTimeOffset expiresAt) =>
                db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO pg_temp.f061_second_review_session_probe (
                        review_session_id, mirror_id, decision_nonce,
                        second_reviewer_id, decision,
                        review_evidence_sha256, session_intent_evidence_sha256,
                        issued_at_utc, reviewed_at_utc,
                        consumed_at_utc, expires_at_utc
                    ) VALUES (
                        {session}, {mirror}, {nonce}, {reviewer}, {decision},
                        {reviewSha}, {sessionSha}, {issuedAt}, {reviewedAt},
                        {consumedAt}, {expiresAt}
                    )
                    """);

            // The production journal has a global session PK, a
            // separate nonce UNIQUE and a separate mirror UNIQUE.
            // These must all be preserved on the real PG17 probe.
            Assert.Equal(3L, await db.Database.SqlQueryRaw<long>("""
                SELECT count(*)::bigint AS "Value"
                FROM pg_index
                WHERE indrelid =
                    'pg_temp.f061_second_review_session_probe'::regclass
                    AND indisunique AND indisvalid
                """).SingleAsync());
            var firstSession = Guid.NewGuid();
            var firstMirror = Guid.NewGuid();
            var firstNonce = Guid.NewGuid();
            await Insert(firstSession, firstMirror, firstNonce,
                sha, intentSha, issued, reviewed, consumed, expires);
            Assert.Equal(1L, await db.Database.SqlQueryRaw<long>("""
                SELECT count(*)::bigint AS "Value"
                FROM pg_temp.f061_second_review_session_probe
                """).SingleAsync());

            async Task Rejected(
                Guid session, Guid mirror, Guid nonce,
                string reviewSha, string sessionSha,
                DateTimeOffset issuedAt, DateTimeOffset reviewedAt,
                DateTimeOffset consumedAt, DateTimeOffset expiresAt,
                string expectedSqlState = Npgsql.PostgresErrorCodes.CheckViolation)
            {
                await tx.CreateSavepointAsync("bad_session");
                var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(
                    () => Insert(session, mirror, nonce, reviewSha,
                        sessionSha, issuedAt, reviewedAt,
                        consumedAt, expiresAt));
                Assert.Equal(expectedSqlState, error.SqlState);
                await tx.RollbackToSavepointAsync("bad_session");
                await tx.ReleaseSavepointAsync("bad_session");
            }

            // Each case must fail a real installed CHECK, even without
            // the original journal's unconditionally closed trigger.
            var sameSession = Guid.NewGuid();
            await Rejected(sameSession, Guid.NewGuid(), sameSession,
                sha, intentSha, issued, reviewed, consumed, expires);
            await Rejected(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(),
                sha, intentSha, issued, reviewed, consumed, expires);
            foreach (var bad in new[] {
                new string('a', 63), new string('A', 64)
            })
                await Rejected(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                    bad, intentSha, issued, reviewed, consumed, expires);
            // VARCHAR(64) rejects overlength values *before* executing
            // the CHECK expression: SQLSTATE 22001, not 23514.
            // Assert the real database's first safety fence.
            await Rejected(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                new string('a', 65), intentSha, issued, reviewed,
                consumed, expires, "22001");
            await Rejected(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                sha, new string('B', 64), issued, reviewed, consumed, expires);
            // PostgreSQL timestamptz is microsecond-precise; one
            // .NET tick (100 ns) may be truncated by Npgsql. Use
            // ten ticks (1 us) to exercise the actual SQL CHECK.
            await Rejected(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                sha, intentSha, issued.AddTicks(10), reviewed, consumed,
                expires.AddTicks(10));
            await Rejected(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                sha, intentSha, issued, reviewed.AddTicks(10), consumed, expires);
            await Rejected(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                sha, intentSha, issued, reviewed, consumed.AddTicks(10), expires);
            await Rejected(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                sha, intentSha, issued, reviewed, consumed,
                expires.AddTicks(10));

            // Replay of the *same login* with a DIFFERENT nonce,
            // replay of the nonce with another login, and attempted
            // second review of the SAME mirror must each fail the
            // real PostgreSQL UNIQUE index (SQLSTATE 23505).
            await Rejected(firstSession, Guid.NewGuid(), Guid.NewGuid(),
                sha, intentSha, issued, reviewed, consumed, expires,
                Npgsql.PostgresErrorCodes.UniqueViolation);
            await Rejected(Guid.NewGuid(), Guid.NewGuid(), firstNonce,
                sha, intentSha, issued, reviewed, consumed, expires,
                Npgsql.PostgresErrorCodes.UniqueViolation);
            await Rejected(Guid.NewGuid(), firstMirror, Guid.NewGuid(),
                sha, intentSha, issued, reviewed, consumed, expires,
                Npgsql.PostgresErrorCodes.UniqueViolation);

            // Only the first canonical tuple is present, despite
            // failed CHECK and UNIQUE attempts with SQL savepoints.
            Assert.Equal(1L, await db.Database.SqlQueryRaw<long>("""
                SELECT count(*)::bigint AS "Value"
                FROM pg_temp.f061_second_review_session_probe
                """).SingleAsync());

            // A raw INSERT on the REAL journal is still rejected by
            // its unconditional mutation trigger, regardless of
            // whether a future review SHA looks syntactically valid.
            await tx.CreateSavepointAsync("real_closed");
            var closed = await Assert.ThrowsAsync<Npgsql.PostgresException>(
                () => db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO
                        git_later_checkpoint_mirror_second_review_session_consumptions
                        (review_session_id)
                    VALUES ({Guid.NewGuid()})
                    """));
            Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException,
                closed.SqlState);
            await tx.RollbackToSavepointAsync("real_closed");
            await tx.ReleaseSavepointAsync("real_closed");

            await tx.RollbackAsync();
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
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
        var admin = await harness.LoginAsync(
            harness.AdminName, ApiHarness.AdminPassword);
        var user = await harness.LoginAsync(
            harness.UserName, ApiHarness.UserPassword);
        var superadmin = await harness.LoginAsync(
            harness.SuperadminName, ApiHarness.SuperadminPassword);
        // The preview must postdate the signed reviewer login, or
        // elapsed test-host login time can trigger a false 400 even
        // for an otherwise valid, default-OFF preflight.
        var valid = new {
            targetObservationId = Guid.NewGuid(),
            workpackId = Guid.NewGuid(),
            originalBundleActorId = Guid.NewGuid(),
            sourceRepositoryId = "repo-1",
            expectedTargetReceiptSha256 = new string('a', 64),
            expectedAssessmentEvidenceSha256 = new string('b', 64),
            expectedReviewEvidenceSha256 = new string('c', 64),
            decision = "ApprovedForIndependentReview",
            reviewedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            decisionNonce = Guid.NewGuid()
        };
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
        // The real signed login JWT carries the one-use session UUID.
        // A guessed identical nonce, a noncanonical UTC timestamp or
        // a future review must fail with 400 BEFORE the disabled
        // inspector (which would return 409). No provider is contacted.
        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
            .ReadJwtToken(superadmin);
        var sessionId = Guid.ParseExact(
            jwt.Claims.Single(c => c.Type == "f061_review_session_id").Value,
            "N");
        var badBase = new LaterCheckpointMirrorSecondReviewPreflightRequest(
            valid.targetObservationId, valid.workpackId,
            valid.originalBundleActorId, valid.sourceRepositoryId,
            valid.expectedTargetReceiptSha256,
            valid.expectedAssessmentEvidenceSha256,
            valid.expectedReviewEvidenceSha256, valid.decision,
            valid.reviewedAtUtc, sessionId);
        var nonCanonical = DateTimeOffset.FromUnixTimeMilliseconds(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).AddTicks(1);
        foreach (var invalid in new[] {
            badBase,
            badBase with {
                DecisionNonce = Guid.NewGuid(),
                ReviewedAtUtc = nonCanonical
            },
            badBase with {
                DecisionNonce = Guid.NewGuid(),
                ReviewedAtUtc = DateTimeOffset.UtcNow.AddMinutes(1)
            }
        })
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, route)
            {
                Content = System.Net.Http.Json.JsonContent.Create(invalid)
            };
            message.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", superadmin);
            using var reply = await harness.Client.SendAsync(message);
            Assert.Equal(HttpStatusCode.BadRequest, reply.StatusCode);
            Assert.Contains("LaterCheckpointMirrorSecondReviewPreflightInvalid",
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
