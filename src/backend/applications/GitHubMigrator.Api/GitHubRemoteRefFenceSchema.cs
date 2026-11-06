using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;

// F-042 records observations only. No write permit may be manufactured by
// API, EF, or direct SQL even if a recent observation matches baseline.
public static class GitHubRemoteRefFenceSchema
{
    public static async Task InitializeAsync(
        IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS github_remote_ref_fences (
                id uuid PRIMARY KEY,
                preparation_id uuid NOT NULL REFERENCES incremental_preparations(id),
                plan_id uuid NOT NULL REFERENCES incremental_operation_plans(id),
                workpack_id uuid NOT NULL REFERENCES git_transfer_workpacks(id),
                intent_id uuid NOT NULL REFERENCES github_import_intents(id),
                rehearsal_id uuid NOT NULL REFERENCES git_import_rehearsals(id),
                target_observation_id uuid NOT NULL REFERENCES incremental_target_ref_evidence(id),
                destination_connection_id uuid NOT NULL REFERENCES migration_connections(id),
                destination_validated_at_utc timestamptz NULL,
                manifest_sha256 varchar(64) NOT NULL,
                reconciliation_sha256 varchar(64) NOT NULL,
                bundle_sha256 varchar(64) NOT NULL,
                intent_evidence_sha256 varchar(64) NOT NULL,
                rehearsal_evidence_sha256 varchar(64) NOT NULL,
                target_evidence_sha256 varchar(64) NOT NULL,
                remote_evidence_sha256 varchar(64) NOT NULL,
                remote_ref_fingerprint_sha256 varchar(64) NOT NULL,
                evidence_sha256 varchar(64) NOT NULL,
                status varchar(24) NOT NULL,
                baseline_count integer NOT NULL,
                remote_count integer NOT NULL,
                changed_refs integer NOT NULL,
                observed_at_utc timestamptz NOT NULL,
                expires_at_utc timestamptz NOT NULL,
                actor_id uuid NOT NULL,
                remote_writes_performed boolean NOT NULL DEFAULT false,
                write_capability_verified boolean NOT NULL DEFAULT false,
                execution_authorized boolean NOT NULL DEFAULT false,
                ready_for_execution boolean NOT NULL DEFAULT false
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_github_remote_ref_fence_proof
                ON github_remote_ref_fences(rehearsal_id, remote_evidence_sha256);
            CREATE INDEX IF NOT EXISTS ix_github_remote_ref_fences_rehearsal
                ON github_remote_ref_fences(rehearsal_id, observed_at_utc DESC, id);
            DO $f042$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conrelid = 'github_remote_ref_fences'::regclass
                      AND conname = 'ck_github_remote_ref_fences_safety'
                ) THEN
                    ALTER TABLE github_remote_ref_fences
                    ADD CONSTRAINT ck_github_remote_ref_fences_safety CHECK (
                        remote_writes_performed = false
                        AND write_capability_verified = false
                        AND execution_authorized = false
                        AND ready_for_execution = false
                        AND status IN ('RefSetUnchanged','RemoteDriftBlocked')
                        AND baseline_count BETWEEN 0 AND 25000
                        AND remote_count BETWEEN 0 AND 25000
                        AND changed_refs BETWEEN 0 AND 50000
                        AND ((status = 'RefSetUnchanged') = (changed_refs = 0))
                        AND (status <> 'RefSetUnchanged' OR baseline_count = remote_count)
                        AND expires_at_utc = observed_at_utc + interval '30 seconds'
                        AND manifest_sha256 ~ '^[0-9a-f]{{64}}$'
                        AND reconciliation_sha256 ~ '^[0-9a-f]{{64}}$'
                        AND bundle_sha256 ~ '^[0-9a-f]{{64}}$'
                        AND intent_evidence_sha256 ~ '^[0-9a-f]{{64}}$'
                        AND rehearsal_evidence_sha256 ~ '^[0-9a-f]{{64}}$'
                        AND target_evidence_sha256 ~ '^[0-9a-f]{{64}}$'
                        AND remote_evidence_sha256 ~ '^[0-9a-f]{{64}}$'
                        AND remote_ref_fingerprint_sha256 ~ '^[0-9a-f]{{64}}$'
                        AND evidence_sha256 ~ '^[0-9a-f]{{64}}$'
                    );
                END IF;
            END
            $f042$;
            """, ct);
    }
}
