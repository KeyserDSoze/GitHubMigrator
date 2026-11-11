using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;

// #119: append-only F-061v2 physical source-only Verified evidence. This
// installs NO worker, Git subprocess, HTTP route or destination authority.
// Requires the separate #118 fenced v2 lease schema (not legacy F-061).
public static class GitIncrementalLaterCheckpointMirrorVerifiedSchema
{
    public static async Task InitializeAsync(
        IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS
                git_later_checkpoint_mirror_verified_evidence (
                mirror_id uuid PRIMARY KEY REFERENCES
                    git_later_checkpoint_mirror_leases(mirror_id)
                    ON DELETE RESTRICT,
                workpack_id uuid NOT NULL UNIQUE REFERENCES
                    git_later_checkpoint_bundle_evidence(workpack_id)
                    ON DELETE RESTRICT,
                evidence_version int NOT NULL,
                preparation_id uuid NOT NULL,
                plan_id uuid NOT NULL,
                source_snapshot_id uuid NOT NULL,
                checkpoint_id uuid NOT NULL,
                target_observation_id uuid NOT NULL,
                worker_id uuid NOT NULL,
                lease_token uuid NOT NULL,
                lease_generation bigint NOT NULL,
                staging_evidence_sha256 varchar(64) NOT NULL,
                full_ref_proof_sha256 varchar(64) NOT NULL,
                bundle_evidence_sha256 varchar(64) NOT NULL,
                bundle_sha256 varchar(64) NOT NULL,
                source_ref_set_sha256 varchar(64) NOT NULL,
                git_object_format varchar(6) NOT NULL,
                object_set_sha256 varchar(64) NOT NULL,
                object_count int NOT NULL,
                physical_verified_at_utc timestamptz NOT NULL,
                recorded_at_utc timestamptz NOT NULL,
                evidence_sha256 varchar(64) NOT NULL,
                durable_evidence_persisted boolean NOT NULL DEFAULT true,
                can_materialize boolean NOT NULL DEFAULT false,
                can_push boolean NOT NULL DEFAULT false,
                can_retry boolean NOT NULL DEFAULT false,
                can_dispatch boolean NOT NULL DEFAULT false,
                migration_complete boolean NOT NULL DEFAULT false
            );
            CREATE UNIQUE INDEX IF NOT EXISTS
                ux_later_f061v2_verified_lease
                ON git_later_checkpoint_mirror_verified_evidence(
                    lease_token, lease_generation);
            DO $later_f061v2_verified_checks$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint WHERE
                    conrelid='git_later_checkpoint_mirror_verified_evidence'::regclass
                    AND conname='ck_later_f061v2_verified_safe') THEN
                    ALTER TABLE
                        git_later_checkpoint_mirror_verified_evidence
                    ADD CONSTRAINT ck_later_f061v2_verified_safe CHECK (
                        evidence_version = 1
                        AND mirror_id != '00000000-0000-0000-0000-000000000000'::uuid
                        AND workpack_id != '00000000-0000-0000-0000-000000000000'::uuid
                        AND worker_id != '00000000-0000-0000-0000-000000000000'::uuid
                        AND lease_token != '00000000-0000-0000-0000-000000000000'::uuid
                        AND lease_generation BETWEEN 1 AND 2000
                        AND staging_evidence_sha256 ~ '^[0-9a-f]{64}$'
                        AND full_ref_proof_sha256 ~ '^[0-9a-f]{64}$'
                        AND bundle_evidence_sha256 ~ '^[0-9a-f]{64}$'
                        AND bundle_sha256 ~ '^[0-9a-f]{64}$'
                        AND source_ref_set_sha256 ~ '^[0-9a-f]{64}$'
                        AND object_set_sha256 ~ '^[0-9a-f]{64}$'
                        AND evidence_sha256 ~ '^[0-9a-f]{64}$'
                        AND git_object_format IN ('sha1','sha256')
                        AND object_count BETWEEN 1 AND 200000
                        AND physical_verified_at_utc <= recorded_at_utc
                        AND recorded_at_utc >= physical_verified_at_utc
                        AND recorded_at_utc <= physical_verified_at_utc
                            + interval '2 minutes'
                        AND durable_evidence_persisted
                        AND NOT can_materialize
                        AND NOT can_push AND NOT can_retry
                        AND NOT can_dispatch AND NOT migration_complete
                    );
                END IF;
            END $later_f061v2_verified_checks$;

            -- The exact v2 physical mirror, worker and still-owned
            -- generation must match its existing PostgreSQL lease.
            -- This does NOT authorize inserting a Verified record on
            -- an expired lease; the writer uses an atomic SQL CAS.
            CREATE OR REPLACE FUNCTION
                git_later_checkpoint_mirror_verified_guard()
            RETURNS trigger LANGUAGE plpgsql AS $f061v2_verified_guard$
            DECLARE held record;
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'F-061v2 Verified evidence is immutable';
                END IF;
                SELECT * INTO held FROM git_later_checkpoint_mirror_leases
                    WHERE mirror_id = NEW.mirror_id;
                IF NOT FOUND
                    OR held.workpack_id IS DISTINCT FROM NEW.workpack_id
                    OR held.preparation_id IS DISTINCT FROM NEW.preparation_id
                    OR held.plan_id IS DISTINCT FROM NEW.plan_id
                    OR held.source_snapshot_id IS DISTINCT FROM NEW.source_snapshot_id
                    OR held.checkpoint_id IS DISTINCT FROM NEW.checkpoint_id
                    OR held.target_observation_id IS DISTINCT FROM NEW.target_observation_id
                    OR held.worker_id IS DISTINCT FROM NEW.worker_id
                    OR held.lease_token IS DISTINCT FROM NEW.lease_token
                    OR held.generation IS DISTINCT FROM NEW.lease_generation
                    OR held.staging_evidence_sha256 IS DISTINCT FROM NEW.staging_evidence_sha256
                    OR held.source_full_ref_proof_sha256 IS DISTINCT FROM NEW.full_ref_proof_sha256
                    OR held.bundle_evidence_sha256 IS DISTINCT FROM NEW.bundle_evidence_sha256
                    OR held.bundle_sha256 IS DISTINCT FROM NEW.bundle_sha256
                    OR held.source_ref_set_sha256 IS DISTINCT FROM NEW.source_ref_set_sha256
                    OR held.state <> 'Held'
                    OR held.lease_until_utc <= NEW.recorded_at_utc
                    OR NEW.physical_verified_at_utc < held.acquired_at_utc
                    THEN
                    RAISE EXCEPTION
                        'F-061v2 Verified proof is not owned by a live lease';
                END IF;
                RETURN NEW;
            END $f061v2_verified_guard$;

            DO $later_f061v2_verified_triggers$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_trigger
                    WHERE tgrelid =
                        'git_later_checkpoint_mirror_verified_evidence'::regclass
                      AND tgname='trg_later_f061v2_verified_guard') THEN
                    CREATE TRIGGER trg_later_f061v2_verified_guard
                        BEFORE INSERT OR UPDATE OR DELETE
                        ON git_later_checkpoint_mirror_verified_evidence
                        FOR EACH ROW EXECUTE FUNCTION
                            git_later_checkpoint_mirror_verified_guard();
                END IF;
            END $later_f061v2_verified_triggers$;
            """, ct);
    }
}
