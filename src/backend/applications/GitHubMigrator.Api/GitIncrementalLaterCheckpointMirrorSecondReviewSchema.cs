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
            END $f061v2e_review_triggers$;
            """, ct);
    }
}
