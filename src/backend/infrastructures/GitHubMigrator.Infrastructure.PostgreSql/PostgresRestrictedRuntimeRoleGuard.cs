using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// #133: fail-closed startup check for the independently provisioned
// PostgreSQL LOGIN used only after a trusted bootstrap/migration pass.
// This checks the ACTUAL database session role and installed triggers;
// no JWT claim, client-set GUC, API header or connection-string flag can
// spoof catalog privileges. It does not provision grants or enable #130.
public static class PostgresRestrictedRuntimeRoleGuard
{
    public static async Task RequireSafeAsync(
        AppDbContext db, CancellationToken ct = default)
    {
        var isRestricted = await db.Database.SqlQueryRaw<bool>("""
            WITH protected AS (
                SELECT c.oid, c.relowner, c.relname
                FROM pg_catalog.pg_class c
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public'
                  AND c.relname IN (
                      'git_later_checkpoint_mirror_second_reviews',
                      'git_later_checkpoint_mirror_second_review_events',
                      'git_later_checkpoint_mirror_second_review_session_consumptions'
                  )
                  AND c.relkind IN ('r', 'p')
            ),
            protected_triggers AS (
                SELECT t.oid, p.proowner
                FROM pg_catalog.pg_trigger t
                JOIN protected c ON c.oid = t.tgrelid
                JOIN pg_catalog.pg_proc p ON p.oid = t.tgfoid
                WHERE NOT t.tgisinternal
                  AND t.tgenabled IN ('O', 'A')
                  AND (
                      (c.relname = 'git_later_checkpoint_mirror_second_reviews'
                       AND t.tgname = 'trg_f061v2e_second_review_closed')
                      OR
                      (c.relname = 'git_later_checkpoint_mirror_second_review_events'
                       AND t.tgname = 'trg_f061v2e_second_review_event_closed')
                      OR
                      (c.relname = 'git_later_checkpoint_mirror_second_review_session_consumptions'
                       AND t.tgname = 'trg_f061v2e_second_review_session_closed')
                  )
            )
            SELECT (
                NOT role.rolsuper AND NOT role.rolcreatedb
                AND NOT role.rolcreaterole AND NOT role.rolreplication
                AND NOT role.rolbypassrls
                AND NOT pg_catalog.has_database_privilege(
                    CURRENT_USER, CURRENT_DATABASE(), 'CREATE')
                AND NOT pg_catalog.has_schema_privilege(
                    CURRENT_USER, 'public', 'CREATE')
                AND NOT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_database d
                    WHERE d.datname = CURRENT_DATABASE()
                      AND pg_catalog.pg_has_role(
                          role.rolname, d.datdba, 'MEMBER')
                )
                AND (SELECT count(*) FROM protected) = 3
                AND (SELECT count(*) FROM protected_triggers) = 3
                AND NOT EXISTS (
                    SELECT 1 FROM protected c
                    WHERE pg_catalog.pg_has_role(
                        role.rolname, c.relowner, 'MEMBER')
                       OR pg_catalog.has_table_privilege(
                        role.rolname, c.oid, 'INSERT, UPDATE, DELETE')
                )
                AND NOT EXISTS (
                    SELECT 1 FROM protected_triggers t
                    WHERE pg_catalog.pg_has_role(
                        role.rolname, t.proowner, 'MEMBER')
                )
            ) AS "Value"
            FROM pg_catalog.pg_roles role
            WHERE role.rolname = CURRENT_USER
            """).SingleOrDefaultAsync(ct);
        if (!isRestricted)
            throw new InvalidOperationException(
                "PostgreSqlRuntimeRoleNotRestricted: runtime-only startup " +
                "requires an independently provisioned non-owner, " +
                "non-superuser login and all three enabled F-061 review " +
                "mutation-denial triggers. Use privileged schema bootstrap " +
                "separately; never reuse its credentials for the API.");
    }
}
