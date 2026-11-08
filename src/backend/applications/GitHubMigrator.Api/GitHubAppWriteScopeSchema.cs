using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;

// F-043 certifies a transient GitHub App token response, NOT authorization
// to execute GitHub writes. No token or private key appears in the schema.
public static class GitHubAppWriteScopeSchema
{
    public static async Task InitializeAsync(
        IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS github_app_write_scopes (
                id uuid PRIMARY KEY,
                preparation_id uuid NOT NULL REFERENCES incremental_preparations(id),
                plan_id uuid NOT NULL REFERENCES incremental_operation_plans(id),
                fence_id uuid NOT NULL UNIQUE REFERENCES github_remote_ref_fences(id),
                workpack_id uuid NOT NULL REFERENCES git_transfer_workpacks(id),
                intent_id uuid NOT NULL REFERENCES github_import_intents(id),
                rehearsal_id uuid NOT NULL REFERENCES git_import_rehearsals(id),
                destination_connection_id uuid NOT NULL REFERENCES migration_connections(id),
                organization varchar(100) NOT NULL,
                repository varchar(100) NOT NULL,
                installation_id bigint NOT NULL,
                repository_id bigint NOT NULL,
                manifest_sha256 varchar(64) NOT NULL,
                bundle_sha256 varchar(64) NOT NULL,
                intent_evidence_sha256 varchar(64) NOT NULL,
                rehearsal_evidence_sha256 varchar(64) NOT NULL,
                fence_evidence_sha256 varchar(64) NOT NULL,
                remote_ref_fingerprint_sha256 varchar(64) NOT NULL,
                evidence_sha256 varchar(64) NOT NULL,
                status varchar(32) NOT NULL,
                observed_at_utc timestamptz NOT NULL,
                token_expires_at_utc timestamptz NOT NULL,
                actor_id uuid NOT NULL,
                scoped_contents_write_reported boolean NOT NULL,
                write_capability_verified boolean NOT NULL DEFAULT false,
                execution_authorized boolean NOT NULL DEFAULT false,
                remote_writes_performed boolean NOT NULL DEFAULT false,
                ready_for_execution boolean NOT NULL DEFAULT false
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_github_app_write_scope_fence
                ON github_app_write_scopes(fence_id);
            CREATE INDEX IF NOT EXISTS ix_github_app_write_scope_intent
                ON github_app_write_scopes(intent_id, observed_at_utc DESC, id);
            DO $f043$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conrelid = 'github_app_write_scopes'::regclass
                      AND conname = 'ck_github_app_write_scopes_safety'
                ) THEN
                    ALTER TABLE github_app_write_scopes
                    ADD CONSTRAINT ck_github_app_write_scopes_safety CHECK (
                        scoped_contents_write_reported = true
                        AND write_capability_verified = false
                        AND execution_authorized = false
                        AND remote_writes_performed = false
                        AND ready_for_execution = false
                        AND installation_id > 0 AND repository_id > 0
                        AND status = 'ScopedContentsWriteIssued'
                        AND token_expires_at_utc >
                            observed_at_utc + interval '2 minutes'
                        AND token_expires_at_utc <=
                            observed_at_utc + interval '65 minutes'
                        AND manifest_sha256 ~ '^[0-9a-f]{{64}}$'
                        AND bundle_sha256 ~ '^[0-9a-f]{{64}}$'
                        AND intent_evidence_sha256 ~ '^[0-9a-f]{{64}}$'
                        AND rehearsal_evidence_sha256 ~ '^[0-9a-f]{{64}}$'
                        AND fence_evidence_sha256 ~ '^[0-9a-f]{{64}}$'
                        AND remote_ref_fingerprint_sha256 ~ '^[0-9a-f]{{64}}$'
                        AND evidence_sha256 ~ '^[0-9a-f]{{64}}$'
                    );
                END IF;
            END
            $f043$;
            """, ct);
    }
}
