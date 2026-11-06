using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;

// Additive source-only rehearsal evidence table. PostgreSQL CHECK is installed
// EVEN when EF EnsureCreated initially created the table without constraints.
public static class GitImportRehearsalSchema
{
    public static async Task InitializeAsync(
        IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS git_import_rehearsals (
                id uuid PRIMARY KEY,
                intent_id uuid NOT NULL UNIQUE REFERENCES github_import_intents(id),
                preparation_id uuid NOT NULL REFERENCES incremental_preparations(id),
                plan_id uuid NOT NULL REFERENCES incremental_operation_plans(id),
                workpack_id uuid NOT NULL REFERENCES git_transfer_workpacks(id),
                intent_evidence_sha256 varchar(64) NOT NULL,
                manifest_sha256 varchar(64) NOT NULL,
                reconciliation_sha256 varchar(64) NOT NULL,
                bundle_sha256 varchar(64) NOT NULL,
                safe_ref_plan_sha256 varchar(64) NOT NULL,
                evidence_sha256 varchar(64) NOT NULL,
                git_object_format varchar(6) NOT NULL,
                verified_source_refs_sha256 varchar(64) NOT NULL,
                create_candidates integer NOT NULL,
                already_matching integer NOT NULL,
                target_only_not_materialized integer NOT NULL,
                destination_created integer NOT NULL,
                created_refs_sha256 varchar(64) NOT NULL,
                completed_at_utc timestamptz NOT NULL,
                requested_by uuid NOT NULL,
                destination_writes_performed boolean NOT NULL DEFAULT false,
                execution_authorized boolean NOT NULL DEFAULT false,
                ready_for_execution boolean NOT NULL DEFAULT false
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_git_import_rehearsals_intent
                ON git_import_rehearsals(intent_id);
            CREATE INDEX IF NOT EXISTS ix_git_import_rehearsals_workpack
                ON git_import_rehearsals(workpack_id, completed_at_utc DESC, id);
            DO $f041$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conrelid = 'git_import_rehearsals'::regclass
                        AND conname = 'ck_git_import_rehearsals_safety'
                ) THEN
                    ALTER TABLE git_import_rehearsals
                    ADD CONSTRAINT ck_git_import_rehearsals_safety CHECK (
                        destination_writes_performed = false
                        AND execution_authorized = false
                        AND ready_for_execution = false
                        AND git_object_format IN ('sha1','sha256')
                        AND create_candidates BETWEEN 1 AND 128
                        AND already_matching BETWEEN 0 AND 25000
                        AND target_only_not_materialized BETWEEN 0 AND 25000
                        AND destination_created = create_candidates
                        AND intent_evidence_sha256 ~ '^[0-9a-f]{{64}}$'
                        AND manifest_sha256 ~ '^[0-9a-f]{{64}}$'
                        AND reconciliation_sha256 ~ '^[0-9a-f]{{64}}$'
                        AND bundle_sha256 ~ '^[0-9a-f]{{64}}$'
                        AND safe_ref_plan_sha256 ~ '^[0-9a-f]{{64}}$'
                        AND evidence_sha256 ~ '^[0-9a-f]{{64}}$'
                        AND verified_source_refs_sha256 ~ '^[0-9a-f]{{64}}$'
                        AND created_refs_sha256 ~ '^[0-9a-f]{{64}}$'
                    );
                END IF;
            END
            $f041$;
            """, ct);
    }
}
