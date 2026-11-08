using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;

// F-039 additive phase-0 schema. Production requires reviewed versioned
// migrations; constraints retrofit existing EnsureCreated() tables.
public static class GitHubImportReadinessSchema
{
    public static async Task InitializeAsync(
        IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS github_import_readiness (
                id uuid PRIMARY KEY,
                preparation_id uuid NOT NULL REFERENCES incremental_preparations(id),
                plan_id uuid NOT NULL REFERENCES incremental_operation_plans(id),
                workpack_id uuid NOT NULL REFERENCES git_transfer_workpacks(id),
                reconciliation_id uuid NOT NULL REFERENCES incremental_git_ref_reconciliations(id),
                source_repository_id varchar(256) NOT NULL,
                target_repository_name varchar(100) NOT NULL,
                organization varchar(100) NOT NULL,
                destination_connection_id uuid NOT NULL REFERENCES migration_connections(id),
                destination_validated_at_utc timestamptz NULL,
                manifest_sha256 varchar(64) NOT NULL,
                reconciliation_sha256 varchar(64) NOT NULL,
                target_evidence_sha256 varchar(64) NOT NULL,
                source_bundle_sha256 varchar(64) NOT NULL,
                evidence_sha256 varchar(64) NOT NULL,
                observed_at_utc timestamptz NOT NULL,
                repository_status varchar(32) NOT NULL,
                reported_push varchar(24) NOT NULL,
                archived boolean NOT NULL,
                disabled boolean NOT NULL,
                diverged_refs integer NOT NULL,
                target_only_refs integer NOT NULL,
                created_by uuid NOT NULL,
                write_capability_verified boolean NOT NULL DEFAULT false,
                ready_for_execution boolean NOT NULL DEFAULT false
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_github_import_readiness_proof
                ON github_import_readiness(workpack_id, evidence_sha256);
            CREATE INDEX IF NOT EXISTS ix_github_import_readiness_plan
                ON github_import_readiness(plan_id, observed_at_utc DESC, id);
            CREATE INDEX IF NOT EXISTS ix_github_import_readiness_workpack
                ON github_import_readiness(workpack_id, observed_at_utc DESC, id);
            DO $f039$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conrelid = 'github_import_readiness'::regclass
                      AND conname = 'ck_github_import_readiness_safety'
                ) THEN
                    ALTER TABLE github_import_readiness
                    ADD CONSTRAINT ck_github_import_readiness_safety CHECK (
                        ready_for_execution = false
                        AND write_capability_verified = false
                        AND repository_status IN
                            ('Visible','NotVisibleOrMissing','Unauthorized',
                             'Forbidden','RateLimited','Unavailable','InvalidResponse')
                        AND reported_push IN
                            ('ReportedAllowed','ReportedDenied','Unverified')
                        AND diverged_refs BETWEEN 0 AND 25000
                        AND target_only_refs BETWEEN 0 AND 25000
                        AND manifest_sha256 ~ '^[0-9a-f]+$'
                        AND length(manifest_sha256) = 64
                        AND reconciliation_sha256 ~ '^[0-9a-f]+$'
                        AND length(reconciliation_sha256) = 64
                        AND target_evidence_sha256 ~ '^[0-9a-f]+$'
                        AND length(target_evidence_sha256) = 64
                        AND source_bundle_sha256 ~ '^[0-9a-f]+$'
                        AND length(source_bundle_sha256) = 64
                        AND evidence_sha256 ~ '^[0-9a-f]+$'
                        AND length(evidence_sha256) = 64
                    );
                END IF;
            END
            $f039$;
            """, ct);
    }
}
