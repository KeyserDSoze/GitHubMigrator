using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;

// #129 foundation: additive, non-authorizing second-review history.
// Deliberately NO writer, JWT route, worker, lease or provider operation.
// Both tables fail ALL INSERT/UPDATE/DELETE until an independently
// authenticated SQL+POSIX-locked review writer is designed and tested.
public static class GitIncrementalLaterCheckpointMirrorSecondReviewSchema
{
    public static async Task InitializeAsync(
        IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // EF Core ExecuteSqlRawAsync uses composite formatting:
        // PostgreSQL's {64} SHA regex quantifier must be {{64}} here.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS
                git_later_checkpoint_mirror_second_reviews (
                mirror_id uuid PRIMARY KEY REFERENCES
                    git_later_checkpoint_mirror_generation_assessments(mirror_id)
                    ON DELETE RESTRICT,
                workpack_id uuid NOT NULL UNIQUE REFERENCES
                    git_later_checkpoint_bundle_evidence(workpack_id)
                    ON DELETE RESTRICT,
                evidence_version integer NOT NULL,
                checkpoint_id uuid NOT NULL REFERENCES
                    github_source_sync_checkpoints(id) ON DELETE RESTRICT,
                source_snapshot_id uuid NOT NULL REFERENCES
                    staging_snapshot_syncs(snapshot_id) ON DELETE RESTRICT,
                first_reviewer_id uuid NOT NULL,
                second_reviewer_id uuid NOT NULL,
                original_bundle_actor_id uuid NOT NULL,
                original_worker_id uuid NOT NULL,
                original_lease_token uuid NOT NULL,
                original_generation bigint NOT NULL,
                suggested_next_generation bigint NOT NULL,
                decision varchar(40) NOT NULL,
                first_decision_sha256 varchar(64) NOT NULL,
                isolation_receipt_sha256 varchar(64) NOT NULL,
                assessment_evidence_sha256 varchar(64) NOT NULL,
                staging_evidence_sha256 varchar(64) NOT NULL,
                source_full_ref_proof_sha256 varchar(64) NOT NULL,
                source_ref_set_sha256 varchar(64) NOT NULL,
                target_receipt_sha256 varchar(64) NOT NULL,
                bundle_sha256 varchar(64) NOT NULL,
                prepared_intent_sha256 varchar(64) NOT NULL,
                object_set_sha256 varchar(64) NOT NULL,
                object_count integer NOT NULL,
                source_reviewed_at_utc timestamptz NOT NULL,
                reviewed_at_utc timestamptz NOT NULL,
                evidence_sha256 varchar(64) NOT NULL,
                durable_second_review_persisted boolean NOT NULL DEFAULT false,
                can_readmit boolean NOT NULL DEFAULT false,
                can_materialize boolean NOT NULL DEFAULT false,
                can_push boolean NOT NULL DEFAULT false,
                can_retry boolean NOT NULL DEFAULT false,
                can_dispatch boolean NOT NULL DEFAULT false,
                migration_complete boolean NOT NULL DEFAULT false,
                CONSTRAINT ck_f061v2e_second_review_safe CHECK (
                    evidence_version = 1
                    AND first_reviewer_id !=
                        '00000000-0000-0000-0000-000000000000'::uuid
                    AND second_reviewer_id !=
                        '00000000-0000-0000-0000-000000000000'::uuid
                    AND original_bundle_actor_id !=
                        '00000000-0000-0000-0000-000000000000'::uuid
                    AND original_worker_id !=
                        '00000000-0000-0000-0000-000000000000'::uuid
                    AND original_lease_token !=
                        '00000000-0000-0000-0000-000000000000'::uuid
                    AND second_reviewer_id != first_reviewer_id
                    AND second_reviewer_id != original_bundle_actor_id
                    AND second_reviewer_id != original_worker_id
                    AND original_generation BETWEEN 1 AND 1999
                    AND suggested_next_generation = original_generation + 1
                    AND suggested_next_generation BETWEEN 2 AND 2000
                    AND decision IN ('ApprovedForIndependentReview',
                        'RejectedForIndependentReview')
                    AND first_decision_sha256 ~ '^[0-9a-f]{{64}}$'
                    AND isolation_receipt_sha256 ~ '^[0-9a-f]{{64}}$'
                    AND assessment_evidence_sha256 ~ '^[0-9a-f]{{64}}$'
                    AND staging_evidence_sha256 ~ '^[0-9a-f]{{64}}$'
                    AND source_full_ref_proof_sha256 ~ '^[0-9a-f]{{64}}$'
                    AND source_ref_set_sha256 ~ '^[0-9a-f]{{64}}$'
                    AND target_receipt_sha256 ~ '^[0-9a-f]{{64}}$'
                    AND bundle_sha256 ~ '^[0-9a-f]{{64}}$'
                    AND prepared_intent_sha256 ~ '^[0-9a-f]{{64}}$'
                    AND object_set_sha256 ~ '^[0-9a-f]{{64}}$'
                    AND object_count BETWEEN 1 AND 200000
                    AND source_reviewed_at_utc <= reviewed_at_utc
                    AND reviewed_at_utc - source_reviewed_at_utc <=
                        interval '2 minutes'
                    AND evidence_sha256 ~ '^[0-9a-f]{{64}}$'
                    AND NOT durable_second_review_persisted
                    AND NOT can_readmit AND NOT can_materialize
                    AND NOT can_push AND NOT can_retry
                    AND NOT can_dispatch AND NOT migration_complete
                )
            );
            CREATE TABLE IF NOT EXISTS
                git_later_checkpoint_mirror_second_review_events (
                id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                mirror_id uuid NOT NULL UNIQUE REFERENCES
                    git_later_checkpoint_mirror_second_reviews(mirror_id)
                    ON DELETE RESTRICT,
                second_reviewer_id uuid NOT NULL,
                decision varchar(40) NOT NULL,
                evidence_sha256 varchar(64) NOT NULL,
                occurred_at_utc timestamptz NOT NULL,
                CONSTRAINT ck_f061v2e_second_review_event_safe CHECK (
                    decision IN ('ApprovedForIndependentReview',
                        'RejectedForIndependentReview')
                    AND evidence_sha256 ~ '^[0-9a-f]{{64}}$'
                )
            );

            -- #129: every event must be an EXACT second-review
            -- witness, not just a foreign row with a matching mirror
            -- UUID. The compound FK also covers the SHA, reviewer,
            -- explicit approve/reject and exact reviewed timestamp.
            -- This migration is additive/idempotent on installations
            -- that already have the two default-DENY tables.
            CREATE UNIQUE INDEX IF NOT EXISTS
                uq_f061v2e_second_review_event_tuple ON
                git_later_checkpoint_mirror_second_reviews (
                    mirror_id, evidence_sha256, second_reviewer_id,
                    decision, reviewed_at_utc);

            DO $f061v2e_exact_event_fk$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conname =
                        'fk_f061v2e_second_review_exact_event'
                        AND conrelid =
                        'git_later_checkpoint_mirror_second_review_events'::regclass
                ) THEN
                    ALTER TABLE
                        git_later_checkpoint_mirror_second_review_events
                    ADD CONSTRAINT fk_f061v2e_second_review_exact_event
                    FOREIGN KEY (
                        mirror_id, evidence_sha256, second_reviewer_id,
                        decision, occurred_at_utc)
                    REFERENCES
                        git_later_checkpoint_mirror_second_reviews (
                            mirror_id, evidence_sha256, second_reviewer_id,
                            decision, reviewed_at_utc)
                    ON DELETE RESTRICT;
                END IF;
            END $f061v2e_exact_event_fk$;

            -- #129: a review row must reference the EXACT original
            -- Assessed evidence and F-061 terminal worker/token/gen,
            -- source workpack/target and physical receipt, not merely
            -- the same mirror UUID. This FK is additive and remains
            -- active even if a future carefully scoped writer exists.
            -- The parent assessment row is append-only and remains
            -- non-authorizing. No second-review insert is enabled.
            CREATE UNIQUE INDEX IF NOT EXISTS
                uq_f061v2e_assessment_second_review_scope ON
                git_later_checkpoint_mirror_generation_assessments (
                    mirror_id, workpack_id, checkpoint_id,
                    source_snapshot_id, evidence_sha256,
                    original_worker_id, original_lease_token,
                    original_generation, suggested_next_generation,
                    staging_evidence_sha256, target_receipt_sha256,
                    recovery_decision_sha256, isolation_receipt_sha256);

            DO $f061v2e_review_generation_fk$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conname =
                        'fk_f061v2e_second_review_generation_scope'
                    AND conrelid =
                        'git_later_checkpoint_mirror_second_reviews'::regclass
                ) THEN
                    ALTER TABLE git_later_checkpoint_mirror_second_reviews
                    ADD CONSTRAINT
                        fk_f061v2e_second_review_generation_scope
                    FOREIGN KEY (
                        mirror_id, workpack_id, checkpoint_id,
                        source_snapshot_id, assessment_evidence_sha256,
                        original_worker_id, original_lease_token,
                        original_generation, suggested_next_generation,
                        staging_evidence_sha256, target_receipt_sha256,
                        first_decision_sha256, isolation_receipt_sha256)
                    REFERENCES git_later_checkpoint_mirror_generation_assessments (
                        mirror_id, workpack_id, checkpoint_id,
                        source_snapshot_id, evidence_sha256,
                        original_worker_id, original_lease_token,
                        original_generation, suggested_next_generation,
                        staging_evidence_sha256, target_receipt_sha256,
                        recovery_decision_sha256, isolation_receipt_sha256)
                    ON DELETE RESTRICT;
                END IF;
            END $f061v2e_review_generation_fk$;

            -- #129: the first 13-field FK intentionally binds the
            -- original worker/lease/generation, target and first human
            -- decision. This second additive, exact-scope FK ALSO
            -- binds physical Git object-closure, full source ref
            -- proof, original bundle and Prepared marker, together
            -- with the original Assessed source-read timestamp.
            -- Neither constraint grants any write permission.
            CREATE UNIQUE INDEX IF NOT EXISTS
                uq_f061v2e_assessment_review_object_scope ON
                git_later_checkpoint_mirror_generation_assessments (
                    mirror_id, source_full_ref_proof_sha256,
                    source_ref_set_sha256, bundle_sha256,
                    prepared_intent_sha256, object_set_sha256,
                    object_count, source_reviewed_at_utc);

            DO $f061v2e_review_object_scope$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conname =
                        'fk_f061v2e_second_review_object_scope'
                    AND conrelid =
                        'git_later_checkpoint_mirror_second_reviews'::regclass
                ) THEN
                    ALTER TABLE git_later_checkpoint_mirror_second_reviews
                    ADD CONSTRAINT
                        fk_f061v2e_second_review_object_scope
                    FOREIGN KEY (
                        mirror_id, source_full_ref_proof_sha256,
                        source_ref_set_sha256, bundle_sha256,
                        prepared_intent_sha256, object_set_sha256,
                        object_count, source_reviewed_at_utc)
                    REFERENCES git_later_checkpoint_mirror_generation_assessments (
                        mirror_id, source_full_ref_proof_sha256,
                        source_ref_set_sha256, bundle_sha256,
                        prepared_intent_sha256, object_set_sha256,
                        object_count, source_reviewed_at_utc)
                    ON DELETE RESTRICT;
                END IF;
            END $f061v2e_review_object_scope$;

            -- #129: future one-shot authenticated review session
            -- consumption. The PK fences a login session globally,
            -- a unique mirror fences second decisions per mirror, and
            -- a unique decision nonce fences cross-session replay.
            -- The five-field FK pins this consumption to the EXACT
            -- second-human review SHA/actor/decision/timestamp.
            -- With the deny-all trigger below, no actor can persist
            -- a consumption or review at present; no writer exists.
            CREATE TABLE IF NOT EXISTS
                git_later_checkpoint_mirror_second_review_session_consumptions (
                review_session_id uuid PRIMARY KEY,
                mirror_id uuid NOT NULL UNIQUE,
                decision_nonce uuid NOT NULL UNIQUE,
                second_reviewer_id uuid NOT NULL,
                decision varchar(40) NOT NULL,
                review_evidence_sha256 varchar(64) NOT NULL,
                -- #130: preserves the exact domain-sealed one-use
                -- session intent, never a bearer authorization.
                session_intent_evidence_sha256 varchar(64) NOT NULL,
                reviewed_at_utc timestamptz NOT NULL,
                issued_at_utc timestamptz NOT NULL,
                consumed_at_utc timestamptz NOT NULL,
                expires_at_utc timestamptz NOT NULL,
                can_readmit boolean NOT NULL DEFAULT false,
                can_materialize boolean NOT NULL DEFAULT false,
                can_push boolean NOT NULL DEFAULT false,
                can_retry boolean NOT NULL DEFAULT false,
                can_dispatch boolean NOT NULL DEFAULT false,
                migration_complete boolean NOT NULL DEFAULT false,
                CONSTRAINT ck_f061v2e_second_review_session_safe CHECK (
                    review_session_id <>
                        '00000000-0000-0000-0000-000000000000'::uuid
                    AND decision_nonce <>
                        '00000000-0000-0000-0000-000000000000'::uuid
                    AND second_reviewer_id <>
                        '00000000-0000-0000-0000-000000000000'::uuid
                    AND decision IN ('ApprovedForIndependentReview',
                        'RejectedForIndependentReview')
                    AND issued_at_utc <= reviewed_at_utc
                    AND reviewed_at_utc <= consumed_at_utc
                    AND consumed_at_utc < expires_at_utc
                    AND expires_at_utc =
                        issued_at_utc + interval '5 minutes'
                    AND consumed_at_utc - reviewed_at_utc <=
                        interval '2 minutes'
                    AND NOT can_readmit AND NOT can_materialize
                    AND NOT can_push AND NOT can_retry
                    AND NOT can_dispatch AND NOT migration_complete
                ),
                CONSTRAINT fk_f061v2e_second_review_session_exact_decision
                    FOREIGN KEY (
                        mirror_id, review_evidence_sha256,
                        second_reviewer_id, decision, reviewed_at_utc)
                    REFERENCES
                        git_later_checkpoint_mirror_second_reviews (
                            mirror_id, evidence_sha256,
                            second_reviewer_id, decision, reviewed_at_utc)
                    ON DELETE RESTRICT
            );

            -- #130: SQL one-use intent parity. On repeat bootstrap
            -- only add a validated, immutable source-scope guard.
            -- This never permits a review/session/event INSERT.
            DO $f061v2e_session_scope_v2$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conname =
                        'ck_f061v2e_second_review_session_scope_v2'
                        AND conrelid =
                            'git_later_checkpoint_mirror_second_review_session_consumptions'::regclass
                ) THEN
                    ALTER TABLE
                        git_later_checkpoint_mirror_second_review_session_consumptions
                    ADD CONSTRAINT
                        ck_f061v2e_second_review_session_scope_v2
                    CHECK (
                        mirror_id <>
                            '00000000-0000-0000-0000-000000000000'::uuid
                        AND review_session_id <> decision_nonce
                        AND review_evidence_sha256 ~ '^[0-9a-f]{{64}}$'
                    );
                END IF;
            END $f061v2e_session_scope_v2$;

            -- #130: additive provenance column and CHECK for
            -- installations bootstrapped before intent sealing existed.
            -- Existing second-review session tables have DENY-ALL
            -- triggers; no previously authorized session rows exist.
            -- Do not invent a default/fabricated intent hash.
            ALTER TABLE
                git_later_checkpoint_mirror_second_review_session_consumptions
            ADD COLUMN IF NOT EXISTS
                session_intent_evidence_sha256 varchar(64) NOT NULL;

            DO $f061v2e_session_intent_sha_v3$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conname =
                        'ck_f061v2e_second_review_session_intent_sha_v3'
                        AND conrelid =
                            'git_later_checkpoint_mirror_second_review_session_consumptions'::regclass
                ) THEN
                    ALTER TABLE
                        git_later_checkpoint_mirror_second_review_session_consumptions
                    ADD CONSTRAINT
                        ck_f061v2e_second_review_session_intent_sha_v3
                    CHECK (
                        session_intent_evidence_sha256 ~ '^[0-9a-f]{{64}}$'
                    );
                END IF;
            END $f061v2e_session_intent_sha_v3$;

            -- #130: SQL matches the exact canonical time
            -- precision of the signed login and domain-sealed intent:
            -- whole seconds for JWT issuance/expiry; milliseconds
            -- for the human review and observed consumption time.
            -- PostgreSQL stores timestamptz instants, so this checks
            -- precision, NOT the originating ISO8601 offset nor
            -- independently trusted DB-clock authority.
            DO $f061v2e_session_precision_v4$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conname =
                        'ck_f061v2e_second_review_session_precision_v4'
                        AND conrelid =
                            'git_later_checkpoint_mirror_second_review_session_consumptions'::regclass
                ) THEN
                    ALTER TABLE
                        git_later_checkpoint_mirror_second_review_session_consumptions
                    ADD CONSTRAINT
                        ck_f061v2e_second_review_session_precision_v4
                    CHECK (
                        issued_at_utc = date_trunc('second', issued_at_utc)
                        AND reviewed_at_utc =
                            date_trunc('milliseconds', reviewed_at_utc)
                        AND consumed_at_utc =
                            date_trunc('milliseconds', consumed_at_utc)
                        AND expires_at_utc =
                            date_trunc('second', expires_at_utc)
                    );
                END IF;
            END $f061v2e_session_precision_v4$;

            -- A proposed second reviewer is NOT a verified JWT actor.
            -- No insert (even with matching SHA) can claim a review
            -- before a future protected, source-first and POSIX-locked
            -- coordinator exists. Both journals are mutation-fenced.
            CREATE OR REPLACE FUNCTION
                git_later_checkpoint_mirror_second_review_deny()
            RETURNS trigger LANGUAGE plpgsql AS $f061v2e_review_closed$
            BEGIN
                RAISE EXCEPTION
                    'F-061v2e independent review writer is disabled';
                RETURN NULL;
            END $f061v2e_review_closed$;

            DO $f061v2e_review_triggers$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_trigger WHERE tgrelid =
                        'git_later_checkpoint_mirror_second_reviews'::regclass
                    AND tgname = 'trg_f061v2e_second_review_closed'
                ) THEN
                    CREATE TRIGGER trg_f061v2e_second_review_closed
                    BEFORE INSERT OR UPDATE OR DELETE ON
                        git_later_checkpoint_mirror_second_reviews
                    FOR EACH ROW EXECUTE FUNCTION
                        git_later_checkpoint_mirror_second_review_deny();
                END IF;
                IF NOT EXISTS (
                    SELECT 1 FROM pg_trigger WHERE tgrelid =
                        'git_later_checkpoint_mirror_second_review_events'::regclass
                    AND tgname = 'trg_f061v2e_second_review_event_closed'
                ) THEN
                    CREATE TRIGGER trg_f061v2e_second_review_event_closed
                    BEFORE INSERT OR UPDATE OR DELETE ON
                        git_later_checkpoint_mirror_second_review_events
                    FOR EACH ROW EXECUTE FUNCTION
                        git_later_checkpoint_mirror_second_review_deny();
                END IF;
                IF NOT EXISTS (
                    SELECT 1 FROM pg_trigger WHERE tgrelid =
                        'git_later_checkpoint_mirror_second_review_session_consumptions'::regclass
                    AND tgname = 'trg_f061v2e_second_review_session_closed'
                ) THEN
                    CREATE TRIGGER trg_f061v2e_second_review_session_closed
                    BEFORE INSERT OR UPDATE OR DELETE ON
                        git_later_checkpoint_mirror_second_review_session_consumptions
                    FOR EACH ROW EXECUTE FUNCTION
                        git_later_checkpoint_mirror_second_review_deny();
                END IF;
            END $f061v2e_review_triggers$;
            """, ct);
    }
}
