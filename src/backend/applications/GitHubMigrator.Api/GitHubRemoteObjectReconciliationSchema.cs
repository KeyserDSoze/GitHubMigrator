using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;

// F-058: SHA-only durable reconciliation proof. No mutable executable state,
// sender, credentials, refspecs, Git URL, or remote write path.
public static class GitHubRemoteObjectReconciliationSchema
{
    public static async Task InitializeAsync(
        IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            DO $f058$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conrelid='github_remote_readback_evidence'::regclass
                      AND conname='uq_f058_readback_provenance'
                ) THEN
                    ALTER TABLE github_remote_readback_evidence
                        ADD CONSTRAINT uq_f058_readback_provenance
                        UNIQUE(id, grant_id, repository_id);
                END IF;
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conrelid='github_remote_transmit_hazards'::regclass
                      AND conname='uq_f058_hazard_provenance'
                ) THEN
                    ALTER TABLE github_remote_transmit_hazards
                        ADD CONSTRAINT uq_f058_hazard_provenance
                        UNIQUE(id, grant_id, repository_id);
                END IF;
            END
            $f058$;
            CREATE TABLE IF NOT EXISTS github_remote_object_reconciliations (
                id uuid PRIMARY KEY,
                grant_id uuid NOT NULL,
                readback_id uuid NOT NULL,
                hazard_id uuid NOT NULL,
                repository_id bigint NOT NULL,
                source_snapshot_id uuid NOT NULL,
                state varchar(40) NOT NULL,
                original_baseline_sha256 varchar(64) NOT NULL,
                candidate_set_sha256 varchar(64) NOT NULL,
                target_ref_set_sha256 varchar(64) NOT NULL,
                remote_ref_observation_sha256 varchar(64) NOT NULL,
                full_object_closure_sha256 varchar(64) NOT NULL,
                remote_mirror_receipt_sha256 varchar(64) NOT NULL,
                evidence_sha256 varchar(64) NOT NULL,
                observed_by uuid NOT NULL,
                verified_at_utc timestamptz NOT NULL,
                recorded_at_utc timestamptz NOT NULL,
                remote_push_invoked boolean NOT NULL DEFAULT false,
                remote_writes_authorized boolean NOT NULL DEFAULT false,
                may_automatically_retry boolean NOT NULL DEFAULT false,
                migration_complete boolean NOT NULL DEFAULT false,
                CONSTRAINT uq_f058_one_per_hazard UNIQUE(hazard_id),
                CONSTRAINT uq_f058_one_per_grant UNIQUE(grant_id),
                CONSTRAINT fk_f058_grant FOREIGN KEY(grant_id,repository_id)
                    REFERENCES github_remote_test_grants(id,repository_id)
                    ON DELETE RESTRICT,
                CONSTRAINT fk_f058_readback
                    FOREIGN KEY(readback_id,grant_id,repository_id)
                    REFERENCES github_remote_readback_evidence(
                        id,grant_id,repository_id)
                    ON DELETE RESTRICT,
                CONSTRAINT fk_f058_hazard
                    FOREIGN KEY(hazard_id,grant_id,repository_id)
                    REFERENCES github_remote_transmit_hazards(
                        id,grant_id,repository_id)
                    ON DELETE RESTRICT,
                CONSTRAINT ck_f058_non_executable CHECK (
                    repository_id > 0
                    AND source_snapshot_id <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND observed_by <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND state = 'FullCopyEvidenceOnly'
                    AND original_baseline_sha256 ~ '^[0-9a-f]{{64}}$'
                    AND candidate_set_sha256 ~ '^[0-9a-f]{{64}}$'
                    AND target_ref_set_sha256 ~ '^[0-9a-f]{{64}}$'
                    AND remote_ref_observation_sha256 ~ '^[0-9a-f]{{64}}$'
                    AND full_object_closure_sha256 ~ '^[0-9a-f]{{64}}$'
                    AND remote_mirror_receipt_sha256 ~ '^[0-9a-f]{{64}}$'
                    AND evidence_sha256 ~ '^[0-9a-f]{{64}}$'
                    AND remote_push_invoked = false
                    AND remote_writes_authorized = false
                    AND may_automatically_retry = false
                    AND migration_complete = false
                )
            );
            CREATE INDEX IF NOT EXISTS ix_f058_repo_recorded
                ON github_remote_object_reconciliations(
                    repository_id,recorded_at_utc DESC);
            CREATE TABLE IF NOT EXISTS github_remote_object_reconciliation_events (
                id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                reconciliation_id uuid NOT NULL
                    REFERENCES github_remote_object_reconciliations(id)
                    ON DELETE RESTRICT,
                type varchar(16) NOT NULL,
                actor_id uuid NOT NULL,
                occurred_at_utc timestamptz NOT NULL,
                CONSTRAINT ck_f058_event_type CHECK (type = 'Recorded')
            );
            CREATE INDEX IF NOT EXISTS ix_f058_events
                ON github_remote_object_reconciliation_events(
                    reconciliation_id,id);
            CREATE OR REPLACE FUNCTION f058_immutable()
            RETURNS trigger LANGUAGE plpgsql AS $reject$
            BEGIN
                RAISE EXCEPTION 'F058 read-only reconciliation journal is immutable';
            END
            $reject$;
            DO $f058_triggers$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_trigger
                    WHERE tgrelid='github_remote_object_reconciliations'::regclass
                      AND tgname='trg_f058_reconciliations_immutable') THEN
                    CREATE TRIGGER trg_f058_reconciliations_immutable
                    BEFORE UPDATE OR DELETE
                    ON github_remote_object_reconciliations
                    FOR EACH ROW EXECUTE FUNCTION f058_immutable();
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_trigger
                    WHERE tgrelid='github_remote_object_reconciliation_events'::regclass
                      AND tgname='trg_f058_events_immutable') THEN
                    CREATE TRIGGER trg_f058_events_immutable
                    BEFORE UPDATE OR DELETE
                    ON github_remote_object_reconciliation_events
                    FOR EACH ROW EXECUTE FUNCTION f058_immutable();
                END IF;
            END
            $f058_triggers$;
            """, ct);
    }
}
