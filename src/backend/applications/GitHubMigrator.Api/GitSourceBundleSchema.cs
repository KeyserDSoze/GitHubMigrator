using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;

// F-035 phase-0 additive schema. Explicit reviewed EF migrations are required
// before production; retrofit CHECK constraints after EF EnsureCreated().
public static class GitSourceBundleSchema
{
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS git_source_bundle_evidence (
                workpack_id uuid PRIMARY KEY
                    REFERENCES git_transfer_workpacks(id) ON DELETE CASCADE,
                plan_id uuid NOT NULL REFERENCES incremental_operation_plans(id),
                reconciliation_id uuid NOT NULL
                    REFERENCES incremental_git_ref_reconciliations(id),
                source_repository_id varchar(256) NOT NULL,
                source_evidence_sha256 varchar(64) NOT NULL,
                reconciliation_sha256 varchar(64) NOT NULL,
                bundle_file_name varchar(40) NOT NULL,
                bundle_sha256 varchar(64) NOT NULL,
                bundle_bytes bigint NOT NULL,
                ref_count integer NOT NULL,
                acquired_at_utc timestamptz NOT NULL,
                ready_for_execution boolean NOT NULL DEFAULT false
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_source_bundle_evidence_filename
                ON git_source_bundle_evidence(bundle_file_name);
            DO $source_bundle$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conrelid = 'git_source_bundle_evidence'::regclass
                      AND conname = 'ck_git_source_bundle_evidence'
                ) THEN
                    ALTER TABLE git_source_bundle_evidence
                    ADD CONSTRAINT ck_git_source_bundle_evidence
                    CHECK (
                        ready_for_execution = false
                        AND bundle_bytes BETWEEN 1 AND 536870912
                        AND ref_count BETWEEN 1 AND 25000
                        AND bundle_file_name ~ '^[0-9a-f]{32}[.]bundle$'
                        AND bundle_sha256 ~ '^[0-9a-f]{64}$'
                        AND source_evidence_sha256 ~ '^[0-9a-f]{64}$'
                        AND reconciliation_sha256 ~ '^[0-9a-f]{64}$'
                    );
                END IF;
            END
            $source_bundle$;
            """, ct);
    }
}
