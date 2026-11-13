using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;

// #127: additive, immutable, NON-AUTHORIZING post-isolation generation
// assessment history. Never modifies terminal F-061v2 leases or grants
// generation ownership; there is no writer, worker or HTTP route.
public static class GitIncrementalLaterCheckpointMirrorGenerationAssessmentSchema
{
    public static async Task InitializeAsync(
        IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // EF Core ExecuteSqlRawAsync applies composite formatting to raw C# string literals:
        // doubled {{64}} reaches PostgreSQL as the correct {64} regex quantifier.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS
                git_later_checkpoint_mirror_generation_assessments (
                mirror_id uuid PRIMARY KEY REFERENCES
                    git_later_checkpoint_mirror_isolation_receipts(mirror_id)
                    ON DELETE RESTRICT,
                workpack_id uuid NOT NULL UNIQUE REFERENCES
                    git_later_checkpoint_bundle_evidence(workpack_id)
                    ON DELETE RESTRICT,
                checkpoint_id uuid NOT NULL REFERENCES
                    github_source_sync_checkpoints(id) ON DELETE RESTRICT,
                source_snapshot_id uuid NOT NULL REFERENCES
                    staging_snapshot_syncs(snapshot_id) ON DELETE RESTRICT,
                evidence_version integer NOT NULL,
                original_worker_id uuid NOT NULL,
                original_lease_token uuid NOT NULL,
                original_generation bigint NOT NULL,
                suggested_next_generation bigint NOT NULL,
                staging_evidence_sha256 varchar(64) NOT NULL,
                source_full_ref_proof_sha256 varchar(64) NOT NULL,
                source_ref_set_sha256 varchar(64) NOT NULL,
                target_receipt_sha256 varchar(64) NOT NULL,
                bundle_sha256 varchar(64) NOT NULL,
                bundle_bytes bigint NOT NULL,
                isolation_receipt_sha256 varchar(64) NOT NULL,
                recovery_decision_sha256 varchar(64) NOT NULL,
                prepared_intent_sha256 varchar(64) NOT NULL,
                isolation_directory_name varchar(160) NOT NULL,
                object_set_sha256 varchar(64) NOT NULL,
                object_count integer NOT NULL,
                source_reviewed_at_utc timestamptz NOT NULL,
                assessed_at_utc timestamptz NOT NULL,
                evidence_sha256 varchar(64) NOT NULL,
                durable_readmission_approved boolean NOT NULL DEFAULT false,
                can_readmit boolean NOT NULL DEFAULT false,
                can_materialize boolean NOT NULL DEFAULT false,
                can_push boolean NOT NULL DEFAULT false,
                can_retry boolean NOT NULL DEFAULT false,
                can_dispatch boolean NOT NULL DEFAULT false,
                migration_complete boolean NOT NULL DEFAULT false,
                CONSTRAINT ck_f061v2e_generation_assessment_safe CHECK (
                    evidence_version = 1
                    AND original_worker_id !=
                        '00000000-0000-0000-0000-000000000000'::uuid
                    AND original_lease_token !=
                        '00000000-0000-0000-0000-000000000000'::uuid
                    AND original_generation BETWEEN 1 AND 1999
                    AND suggested_next_generation = original_generation + 1
                    AND suggested_next_generation BETWEEN 2 AND 2000
                    AND staging_evidence_sha256 ~ '^[0-9a-f]{{64}}$'
                    AND source_full_ref_proof_sha256 ~ '^[0-9a-f]{{64}}$'
                    AND source_ref_set_sha256 ~ '^[0-9a-f]{{64}}$'
                    AND target_receipt_sha256 ~ '^[0-9a-f]{{64}}$'
                    AND bundle_sha256 ~ '^[0-9a-f]{{64}}$'
                    AND bundle_bytes BETWEEN 1 AND 536870912
                    AND isolation_receipt_sha256 ~ '^[0-9a-f]{{64}}$'
                    AND recovery_decision_sha256 ~ '^[0-9a-f]{{64}}$'
                    AND prepared_intent_sha256 ~ '^[0-9a-f]{{64}}$'
                    AND isolation_directory_name =
                        replace(mirror_id::text, '-', '') || '.' ||
                        recovery_decision_sha256 || '.quarantine.git'
                    AND object_set_sha256 ~ '^[0-9a-f]{{64}}$'
                    AND object_count BETWEEN 1 AND 200000
                    AND source_reviewed_at_utc <= assessed_at_utc
                    AND assessed_at_utc - source_reviewed_at_utc <=
                        interval '2 minutes'
                    AND evidence_sha256 ~ '^[0-9a-f]{{64}}$'
                    AND NOT durable_readmission_approved
                    AND NOT can_readmit AND NOT can_materialize
                    AND NOT can_push AND NOT can_retry
                    AND NOT can_dispatch AND NOT migration_complete
                )
            );
            CREATE TABLE IF NOT EXISTS
                git_later_checkpoint_mirror_generation_assessment_events (
                id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                mirror_id uuid NOT NULL UNIQUE REFERENCES
                    git_later_checkpoint_mirror_generation_assessments(mirror_id)
                    ON DELETE RESTRICT,
                evidence_sha256 varchar(64) NOT NULL,
                type varchar(24) NOT NULL DEFAULT 'Assessed',
                occurred_at_utc timestamptz NOT NULL,
                CONSTRAINT ck_f061v2e_generation_assessment_event_safe CHECK (
                    type = 'Assessed'
                    AND evidence_sha256 ~ '^[0-9a-f]{{64}}$'
                )
            );

            -- Upgrade previously bootstrapped audit-only placeholders
            -- without rewriting/deleting historical immutable rows.
            -- Legacy rows are NOT backfilled or silently trusted: the
            -- new NOT VALID check enforces all extra provenance for
            -- subsequent INSERT/UPDATE without scanning old history.
            ALTER TABLE git_later_checkpoint_mirror_generation_assessments
                ADD COLUMN IF NOT EXISTS
                    recovery_decision_sha256 varchar(64),
                ADD COLUMN IF NOT EXISTS
                    prepared_intent_sha256 varchar(64),
                ADD COLUMN IF NOT EXISTS
                    isolation_directory_name varchar(160),
                ADD COLUMN IF NOT EXISTS
                    object_set_sha256 varchar(64),
                ADD COLUMN IF NOT EXISTS
                    object_count integer,
                ADD COLUMN IF NOT EXISTS
                    source_reviewed_at_utc timestamptz;
            DO $generation_scope_upgrade$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conrelid =
                        'git_later_checkpoint_mirror_generation_assessments'::regclass
                        AND conname =
                            'ck_f061v2e_generation_assessment_scope_v2'
                ) THEN
                    ALTER TABLE
                        git_later_checkpoint_mirror_generation_assessments
                    ADD CONSTRAINT ck_f061v2e_generation_assessment_scope_v2
                    CHECK (
                        recovery_decision_sha256 IS NOT NULL
                        AND recovery_decision_sha256 ~ '^[0-9a-f]{{64}}$'
                        AND prepared_intent_sha256 IS NOT NULL
                        AND prepared_intent_sha256 ~ '^[0-9a-f]{{64}}$'
                        AND isolation_directory_name IS NOT NULL
                        AND isolation_directory_name =
                            replace(mirror_id::text, '-', '') || '.' ||
                            recovery_decision_sha256 || '.quarantine.git'
                        AND object_set_sha256 IS NOT NULL
                        AND object_set_sha256 ~ '^[0-9a-f]{{64}}$'
                        AND object_count BETWEEN 1 AND 200000
                        AND source_reviewed_at_utc IS NOT NULL
                        AND source_reviewed_at_utc <= assessed_at_utc
                        AND assessed_at_utc - source_reviewed_at_utc <=
                            interval '2 minutes'
                    ) NOT VALID;
                END IF;
            END $generation_scope_upgrade$;

            -- Database row ancestry is a safety check, NOT proof that
            -- a new lease has been authorized: only future source-first
            -- and independent-review code can establish freshness.
            CREATE OR REPLACE FUNCTION
                git_later_checkpoint_mirror_generation_assessment_guard()
            RETURNS trigger LANGUAGE plpgsql AS $generation_assessment_guard$
            DECLARE
                original_lease record;
                isolation record;
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'F-061v2e generation assessment is immutable';
                END IF;
                SELECT * INTO original_lease
                FROM git_later_checkpoint_mirror_leases
                WHERE mirror_id = NEW.mirror_id;
                SELECT * INTO isolation
                FROM git_later_checkpoint_mirror_isolation_receipts
                WHERE mirror_id = NEW.mirror_id;
                IF original_lease.mirror_id IS NULL
                    OR isolation.mirror_id IS NULL
                    OR original_lease.state <> 'RecoveryRequired'
                    OR original_lease.lease_until_utc IS NOT NULL
                    OR original_lease.has_verified_local_objects
                    OR original_lease.workpack_id <> NEW.workpack_id
                    OR isolation.workpack_id <> NEW.workpack_id
                    OR original_lease.checkpoint_id <> NEW.checkpoint_id
                    OR original_lease.source_snapshot_id <>
                        NEW.source_snapshot_id
                    OR original_lease.worker_id <> NEW.original_worker_id
                    OR original_lease.lease_token <>
                        NEW.original_lease_token
                    OR original_lease.generation <> NEW.original_generation
                    OR isolation.original_worker_id <>
                        NEW.original_worker_id
                    OR isolation.original_lease_token <>
                        NEW.original_lease_token
                    OR isolation.original_generation <>
                        NEW.original_generation
                    OR original_lease.staging_evidence_sha256 <>
                        NEW.staging_evidence_sha256
                    OR original_lease.source_full_ref_proof_sha256 <>
                        NEW.source_full_ref_proof_sha256
                    OR original_lease.source_ref_set_sha256 <>
                        NEW.source_ref_set_sha256
                    OR original_lease.target_receipt_sha256 <>
                        NEW.target_receipt_sha256
                    OR original_lease.bundle_sha256 <> NEW.bundle_sha256
                    OR isolation.bundle_sha256 <> NEW.bundle_sha256
                    OR isolation.bundle_bytes <> NEW.bundle_bytes
                    OR isolation.evidence_sha256 <>
                        NEW.isolation_receipt_sha256
                    OR isolation.recovery_decision_sha256 <>
                        NEW.recovery_decision_sha256
                    OR isolation.intent_sha256 <>
                        NEW.prepared_intent_sha256
                    OR isolation.isolation_directory_name <>
                        NEW.isolation_directory_name
                    OR isolation.physically_verified_object_sha256 <>
                        NEW.object_set_sha256
                    OR isolation.physically_verified_object_count <>
                        NEW.object_count
                    OR isolation.recorded_at_utc > NEW.source_reviewed_at_utc
                    OR NEW.source_reviewed_at_utc > NEW.assessed_at_utc
                    OR NEW.assessed_at_utc - NEW.source_reviewed_at_utc >
                        interval '2 minutes'
                    OR EXISTS (
                        SELECT 1 FROM
                            git_later_checkpoint_mirror_verified_evidence
                        WHERE mirror_id = NEW.mirror_id
                            OR workpack_id = NEW.workpack_id)
                THEN
                    RAISE EXCEPTION
                        'F-061v2e assessed generation history mismatched';
                END IF;
                RETURN NEW;
            END $generation_assessment_guard$;

            CREATE OR REPLACE FUNCTION
                git_later_checkpoint_mirror_generation_assessment_event_guard()
            RETURNS trigger LANGUAGE plpgsql AS $generation_assessment_event$
            DECLARE
                assessed record;
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION
                        'F-061v2e generation assessment audit is immutable';
                END IF;
                SELECT * INTO assessed
                FROM git_later_checkpoint_mirror_generation_assessments
                WHERE mirror_id = NEW.mirror_id;
                IF assessed.mirror_id IS NULL
                    OR NEW.type <> 'Assessed'
                    OR assessed.evidence_sha256 <> NEW.evidence_sha256
                    OR assessed.assessed_at_utc <> NEW.occurred_at_utc
                THEN
                    RAISE EXCEPTION
                        'F-061v2e generation assessment event mismatch';
                END IF;
                RETURN NEW;
            END $generation_assessment_event$;

            DO $generation_assessment_triggers$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_trigger
                    WHERE tgrelid =
                        'git_later_checkpoint_mirror_generation_assessments'::regclass
                        AND tgname = 'trg_f061v2e_generation_assessment'
                ) THEN
                    CREATE TRIGGER trg_f061v2e_generation_assessment
                    BEFORE INSERT OR UPDATE OR DELETE ON
                        git_later_checkpoint_mirror_generation_assessments
                    FOR EACH ROW EXECUTE FUNCTION
                        git_later_checkpoint_mirror_generation_assessment_guard();
                END IF;
                IF NOT EXISTS (
                    SELECT 1 FROM pg_trigger
                    WHERE tgrelid =
                        'git_later_checkpoint_mirror_generation_assessment_events'::regclass
                        AND tgname = 'trg_f061v2e_generation_assessment_event'
                ) THEN
                    CREATE TRIGGER trg_f061v2e_generation_assessment_event
                    BEFORE INSERT OR UPDATE OR DELETE ON
                        git_later_checkpoint_mirror_generation_assessment_events
                    FOR EACH ROW EXECUTE FUNCTION
                        git_later_checkpoint_mirror_generation_assessment_event_guard();
                END IF;
            END $generation_assessment_triggers$;
            """, ct);
    }
}
