using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;

// F-090 is an additive schema bootstrap. Never treat a source checkpoint
// as a target migration, authorization to write, or success signal.
public static class GitHubSourceSyncCheckpointSchema
{
    public static async Task InitializeAsync(
        IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS github_source_sync_checkpoints (
                id uuid PRIMARY KEY,
                preparation_id uuid NOT NULL
                    REFERENCES incremental_preparations(id) ON DELETE RESTRICT,
                plan_id uuid NOT NULL
                    REFERENCES incremental_operation_plans(id) ON DELETE RESTRICT,
                source_connection_id uuid NOT NULL,
                snapshot_id uuid NOT NULL
                    REFERENCES staging_snapshot_syncs(snapshot_id) ON DELETE RESTRICT,
                previous_checkpoint_id uuid NULL
                    REFERENCES github_source_sync_checkpoints(id) ON DELETE RESTRICT,
                manifest_sha256 varchar(64) NOT NULL,
                proof_json text NOT NULL,
                evidence_sha256 varchar(64) NOT NULL,
                evidence_version integer NOT NULL DEFAULT 1,
                ref_change_manifest_json text NULL,
                ref_change_attestation_sha256 varchar(64) NULL,
                recorded_by uuid NOT NULL,
                recorded_at_utc timestamptz NOT NULL,
                can_push boolean NOT NULL DEFAULT false,
                can_retry boolean NOT NULL DEFAULT false,
                can_dispatch boolean NOT NULL DEFAULT false,
                migration_complete boolean NOT NULL DEFAULT false
            );
            -- Safe F-091 upgrade: historic F-090 rows remain version 1.
            ALTER TABLE github_source_sync_checkpoints
                ADD COLUMN IF NOT EXISTS evidence_version integer NOT NULL DEFAULT 1,
                ADD COLUMN IF NOT EXISTS ref_change_manifest_json text NULL,
                ADD COLUMN IF NOT EXISTS ref_change_attestation_sha256 varchar(64) NULL;
            -- Existing databases and EF EnsureCreated may already contain
            -- the table; ALWAYS install the cross-row invariants separately.
            CREATE UNIQUE INDEX IF NOT EXISTS ux_f090_plan_snapshot
                ON github_source_sync_checkpoints(plan_id, snapshot_id);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_f090_plan_predecessor
                ON github_source_sync_checkpoints(plan_id, previous_checkpoint_id);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_f090_plan_baseline
                ON github_source_sync_checkpoints(plan_id)
                WHERE previous_checkpoint_id IS NULL;
            CREATE INDEX IF NOT EXISTS ix_f090_plan_history
                ON github_source_sync_checkpoints(plan_id, recorded_at_utc DESC, id DESC);
            CREATE INDEX IF NOT EXISTS ix_f090_source_snapshot
                ON github_source_sync_checkpoints(source_connection_id, snapshot_id);

            DO $f090_constraint_upgrade$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE
                    conname='ck_f090_evidence_only') THEN
                    ALTER TABLE github_source_sync_checkpoints
                        ADD CONSTRAINT ck_f090_evidence_only CHECK (
                            id <> '00000000-0000-0000-0000-000000000000'::uuid
                            AND preparation_id <> '00000000-0000-0000-0000-000000000000'::uuid
                            AND plan_id <> '00000000-0000-0000-0000-000000000000'::uuid
                            AND source_connection_id <> '00000000-0000-0000-0000-000000000000'::uuid
                            AND snapshot_id <> '00000000-0000-0000-0000-000000000000'::uuid
                            AND recorded_by <> '00000000-0000-0000-0000-000000000000'::uuid
                            AND previous_checkpoint_id IS DISTINCT FROM id
                            AND length(proof_json) BETWEEN 100 AND 16384
                            AND manifest_sha256 ~ '^[0-9a-f]{{64}}$'
                            AND evidence_sha256 ~ '^[0-9a-f]{{64}}$'
                            AND NOT can_push AND NOT can_retry
                            AND NOT can_dispatch AND NOT migration_complete
                        );
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE
                    conname='ck_f091_checkpoint_attestation') THEN
                    ALTER TABLE github_source_sync_checkpoints
                        ADD CONSTRAINT ck_f091_checkpoint_attestation CHECK (
                            (evidence_version = 1
                                AND ref_change_manifest_json IS NULL
                                AND ref_change_attestation_sha256 IS NULL)
                            OR
                            (evidence_version = 2
                                AND length(ref_change_manifest_json) BETWEEN 100 AND 2048
                                AND ref_change_attestation_sha256 ~ '^[0-9a-f]{64}$')
                        );
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE
                    conname='fk_f090_plan') THEN
                    ALTER TABLE github_source_sync_checkpoints
                        ADD CONSTRAINT fk_f090_plan FOREIGN KEY(plan_id)
                        REFERENCES incremental_operation_plans(id) ON DELETE RESTRICT;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE
                    conname='fk_f090_preparation') THEN
                    ALTER TABLE github_source_sync_checkpoints
                        ADD CONSTRAINT fk_f090_preparation FOREIGN KEY(preparation_id)
                        REFERENCES incremental_preparations(id) ON DELETE RESTRICT;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE
                    conname='fk_f090_snapshot') THEN
                    ALTER TABLE github_source_sync_checkpoints
                        ADD CONSTRAINT fk_f090_snapshot FOREIGN KEY(snapshot_id)
                        REFERENCES staging_snapshot_syncs(snapshot_id) ON DELETE RESTRICT;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE
                    conname='fk_f090_predecessor') THEN
                    ALTER TABLE github_source_sync_checkpoints
                        ADD CONSTRAINT fk_f090_predecessor FOREIGN KEY(previous_checkpoint_id)
                        REFERENCES github_source_sync_checkpoints(id) ON DELETE RESTRICT;
                END IF;
            END
            $f090_constraint_upgrade$;

            CREATE TABLE IF NOT EXISTS github_source_sync_checkpoint_events (
                id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                checkpoint_id uuid NOT NULL UNIQUE
                    REFERENCES github_source_sync_checkpoints(id) ON DELETE RESTRICT,
                type varchar(16) NOT NULL,
                actor_id uuid NOT NULL,
                evidence_sha256 varchar(64) NOT NULL,
                occurred_at_utc timestamptz NOT NULL
            );
            DO $f090_event_constraint_upgrade$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE
                    conname='ck_f090_checkpoint_event') THEN
                    ALTER TABLE github_source_sync_checkpoint_events
                        ADD CONSTRAINT ck_f090_checkpoint_event CHECK (
                            type='Recorded'
                            AND actor_id <> '00000000-0000-0000-0000-000000000000'::uuid
                            AND evidence_sha256 ~ '^[0-9a-f]{{64}}$'
                        );
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE
                    conname='fk_f090_event_checkpoint') THEN
                    ALTER TABLE github_source_sync_checkpoint_events
                        ADD CONSTRAINT fk_f090_event_checkpoint FOREIGN KEY(checkpoint_id)
                        REFERENCES github_source_sync_checkpoints(id) ON DELETE RESTRICT;
                END IF;
            END
            $f090_event_constraint_upgrade$;

            CREATE OR REPLACE FUNCTION f090_reject_mutation()
            RETURNS trigger LANGUAGE plpgsql AS $guard$
            BEGIN
                RAISE EXCEPTION 'F090 source sync checkpoint is append-only';
            END
            $guard$;
            DO $f090_triggers$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_trigger
                    WHERE tgrelid='github_source_sync_checkpoints'::regclass
                      AND tgname='trg_f090_checkpoint_immutable') THEN
                    CREATE TRIGGER trg_f090_checkpoint_immutable
                    BEFORE UPDATE OR DELETE ON github_source_sync_checkpoints
                    FOR EACH ROW EXECUTE FUNCTION f090_reject_mutation();
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_trigger
                    WHERE tgrelid='github_source_sync_checkpoint_events'::regclass
                      AND tgname='trg_f090_event_immutable') THEN
                    CREATE TRIGGER trg_f090_event_immutable
                    BEFORE UPDATE OR DELETE ON github_source_sync_checkpoint_events
                    FOR EACH ROW EXECUTE FUNCTION f090_reject_mutation();
                END IF;
            END
            $f090_triggers$;
            """, ct);
    }
}
