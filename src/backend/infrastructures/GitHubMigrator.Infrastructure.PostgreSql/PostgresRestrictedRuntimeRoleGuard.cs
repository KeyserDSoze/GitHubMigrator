using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// #133: fail-closed startup check for the independently provisioned
// PostgreSQL LOGIN used only after a trusted bootstrap/migration pass.
// This checks the ACTUAL database session role and installed triggers;
// no JWT claim, client-set GUC, API header or connection-string flag can
// spoof catalog privileges. It does not provision grants or enable #130.
// SESSION_USER must match CURRENT_USER, so SET ROLE cannot disguise
// the connection's original privileged login.
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
                  -- Same-named triggers redirected to another function
                  -- or protecting only one DML verb are insufficient.
                  AND t.tgfoid =
                      'public.git_later_checkpoint_mirror_second_review_deny()'::regprocedure
                  -- ROW(1) + BEFORE(2) + INSERT(4) + DELETE(8) + UPDATE(16).
                  AND (t.tgtype::integer & 31) = 31
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
                CURRENT_USER = SESSION_USER
                AND NOT role.rolsuper AND NOT role.rolcreatedb
                AND NOT role.rolcreaterole AND NOT role.rolreplication
                AND NOT role.rolbypassrls
                -- NOINHERIT is necessary but not sufficient. A login
                -- member could still SET ROLE into an elevated group,
                -- even though the initial CURRENT_USER passes the
                -- ordinary table/grant checks. Disallow ALL memberships.
                AND role.rolcanlogin AND NOT role.rolinherit
                AND NOT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_auth_members memberships
                    WHERE memberships.member = role.oid
                )
                AND NOT pg_catalog.has_database_privilege(
                    CURRENT_USER, CURRENT_DATABASE(), 'CREATE')
                -- A separately granted CREATE on any other user schema
                -- also grants runtime DDL capability. Check effective
                -- privileges across ALL non-system schemas, including
                -- PUBLIC grants and schemas created after provisioning.
                AND NOT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_namespace user_schema
                    WHERE user_schema.nspname <> 'information_schema'
                      AND pg_catalog.left(user_schema.nspname, 3) <> 'pg_'
                      AND pg_catalog.has_schema_privilege(
                          role.rolname, user_schema.oid, 'CREATE')
                )
                AND NOT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_database d
                    WHERE d.datname = CURRENT_DATABASE()
                      AND pg_catalog.pg_has_role(
                          role.rolname, d.datdba, 'MEMBER')
                )
                -- #133: check ACTUAL effective SQL privileges again at
                -- every runtime-only startup. An account provisioned
                -- safely yesterday may have been over-granted later.
                -- The operator script is not a permanent authorization
                -- barrier, and table-level REVOKE does not necessarily
                -- clear a separate column-level secret GRANT.
                AND pg_catalog.to_regclass(
                    'public.migration_connections') IS NOT NULL
                AND pg_catalog.to_regclass(
                    'public.migration_connection_secrets') IS NOT NULL
                AND NOT pg_catalog.has_column_privilege(
                    role.rolname, 'public.migration_connections',
                    'secret_reference', 'SELECT')
                AND NOT pg_catalog.has_table_privilege(
                    role.rolname, 'public.migration_connection_secrets',
                    'SELECT, INSERT, UPDATE, DELETE')
                -- This phase grants only read-only source discovery and
                -- metadata column listing. A later unauthorized DML
                -- grant would otherwise permit a restricted login to
                -- overwrite source inventory or connection metadata,
                -- even when the original provisioning script was safe.
                AND pg_catalog.to_regclass(
                    'public.migration_discoveries') IS NOT NULL
                AND NOT pg_catalog.has_table_privilege(
                    role.rolname, 'public.migration_connections',
                    'INSERT, UPDATE, DELETE')
                AND NOT pg_catalog.has_table_privilege(
                    role.rolname, 'public.migration_discoveries',
                    'INSERT, UPDATE, DELETE')
                -- #138: even SELECT alone can leak source credentials
                -- through a superuser-owned view (default view-owner
                -- base-table privileges). Permit reads only on the
                -- explicitly reviewed Identity/metadata pilot relations.
                -- has_any_column_privilege catches both table SELECT
                -- and independent SELECT(column) grants, including PUBLIC.
                -- migration_connections secret_reference remains barred
                -- by the separate per-column check above.
                AND NOT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_class readable
                    JOIN pg_catalog.pg_namespace schema_ns
                      ON schema_ns.oid = readable.relnamespace
                    WHERE readable.relkind IN ('r', 'p', 'v', 'm', 'f')
                      AND schema_ns.nspname <> 'information_schema'
                      AND pg_catalog.left(schema_ns.nspname, 3) <> 'pg_'
                      AND NOT (
                          schema_ns.nspname = 'public'
                          AND readable.relname IN (
                              'AspNetUsers', 'AspNetRoles', 'AspNetUserRoles',
                          'AspNetUserClaims', 'AspNetUserLogins',
                          'AspNetUserTokens', 'AspNetRoleClaims',
                          'migration_discoveries', 'migration_connections'
                          )
                      )
                      AND pg_catalog.has_any_column_privilege(
                          role.rolname, readable.oid, 'SELECT')
                )
                -- This is an intentionally READ-ONLY runtime pilot:
                -- PostgreSQL's has_table_privilege ignores a separate
                -- GRANT UPDATE(one_column). Inspect both table AND
                -- per-column permissions on every user relation, not
                -- only explicitly named connection/discovery tables.
                -- The sole DML exception is Identity AspNetUsers UPDATE
                -- (TRUNCATE/TRIGGER are never allowed, including Identity).
                -- (password/lockout/security-stamp lifecycle).
                AND NOT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_class writable
                    JOIN pg_catalog.pg_namespace ns
                      ON ns.oid = writable.relnamespace
                    WHERE writable.relkind IN ('r', 'p', 'v', 'f', 'm')
                      AND ns.nspname <> 'information_schema'
                      AND pg_catalog.left(ns.nspname, 3) <> 'pg_'
                      AND (
                          -- TRUNCATE bypasses DELETE row triggers, including
                          -- the F-061 second-review journal DENY-ALL triggers.
                          -- TRIGGER may alter behavior without table DML.
                          -- PostgreSQL17 MAINTAIN permits relation locks,
                          -- REINDEX and REFRESH; REFERENCES permits FKs.
                          -- Neither belongs to this read-only pilot.
                          pg_catalog.has_table_privilege(
                              role.rolname, writable.oid, 'TRUNCATE, TRIGGER, MAINTAIN, REFERENCES')
                          -- Table privileges do not reveal an independent
                          -- REFERENCES(one_column) GRANT; a malicious
                          -- FK can execute with the referenced owner's
                          -- privileges. The AspNetUsers UPDATE exception
                          -- must not exempt column REFERENCES either.
                          OR EXISTS (
                              SELECT 1
                              FROM pg_catalog.pg_attribute column_acl
                              WHERE column_acl.attrelid = writable.oid
                                AND column_acl.attnum > 0
                                AND NOT column_acl.attisdropped
                                AND pg_catalog.has_column_privilege(
                                    role.rolname, writable.oid,
                                    column_acl.attname, 'REFERENCES')
                          )
                          OR (
                              ns.nspname = 'public'
                              AND writable.relname = 'AspNetUsers'
                              AND (
                                  pg_catalog.has_table_privilege(
                                      role.rolname, writable.oid,
                                      'INSERT, DELETE')
                                  OR EXISTS (
                                      SELECT 1
                                      FROM pg_catalog.pg_attribute a
                                      WHERE a.attrelid = writable.oid
                                        AND a.attnum > 0
                                        AND NOT a.attisdropped
                                        AND pg_catalog.has_column_privilege(
                                            role.rolname, writable.oid,
                                            a.attname, 'INSERT')
                                  )
                              )
                          )
                          OR (
                              NOT (
                                  ns.nspname = 'public'
                                  AND writable.relname = 'AspNetUsers'
                              )
                              AND (
                                  pg_catalog.has_table_privilege(
                                      role.rolname, writable.oid,
                                      'INSERT, UPDATE, DELETE')
                                  OR EXISTS (
                                      SELECT 1
                                      FROM pg_catalog.pg_attribute a
                                      WHERE a.attrelid = writable.oid
                                        AND a.attnum > 0
                                        AND NOT a.attisdropped
                                        AND pg_catalog.has_column_privilege(
                                            role.rolname, writable.oid,
                                            a.attname, 'INSERT, UPDATE')
                                  )
                              )
                          )
                      )
                )
                -- #137: the owner of a SECURITY DEFINER routine can be
                -- privileged even when the caller's relation/DDL grants
                -- are clean. This pilot has no allowlisted definer
                -- entrypoint; reject effective EXECUTE, including PUBLIC
                -- default grants, on all non-system routines.
                AND NOT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_proc routine
                    JOIN pg_catalog.pg_namespace ns
                      ON ns.oid = routine.pronamespace
                    WHERE routine.prosecdef
                      AND ns.nspname <> 'information_schema'
                      AND pg_catalog.left(ns.nspname, 3) <> 'pg_'
                      AND pg_catalog.has_function_privilege(
                          role.rolname, routine.oid, 'EXECUTE')
                )
                -- A sequence can be advanced or reset via USAGE/UPDATE
                -- without INSERT/UPDATE/DELETE permission on its table.
                -- This runtime-only pilot does not need writable sequences.
                AND NOT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_class sequence_relation
                    JOIN pg_catalog.pg_namespace ns
                      ON ns.oid = sequence_relation.relnamespace
                    -- PostgreSQL may reorder independent WHERE predicates;
                    -- has_sequence_privilege() throws SQLSTATE 42809 on
                    -- TOAST tables. CASE must guard the function itself.
                    WHERE CASE
                        WHEN sequence_relation.relkind = 'S'
                          AND ns.nspname <> 'information_schema'
                          AND pg_catalog.left(ns.nspname, 3) <> 'pg_'
                        THEN pg_catalog.has_sequence_privilege(
                            role.rolname, sequence_relation.oid, 'USAGE, UPDATE')
                        ELSE false
                    END
                )
                AND (SELECT count(*) FROM protected) = 3
                AND (SELECT count(*) FROM protected_triggers) = 3
                AND NOT EXISTS (
                    SELECT 1 FROM protected c
                    WHERE pg_catalog.pg_has_role(
                        role.rolname, c.relowner, 'MEMBER')
                       OR pg_catalog.has_table_privilege(
                        role.rolname, c.oid, 'INSERT, UPDATE, DELETE, TRUNCATE, TRIGGER, MAINTAIN, REFERENCES')
                )
                AND NOT EXISTS (
                    SELECT 1 FROM protected_triggers t
                    WHERE pg_catalog.pg_has_role(
                        role.rolname, t.proowner, 'MEMBER')
                )
            ) AS "Value"
            FROM pg_catalog.pg_roles role
            WHERE role.rolname = SESSION_USER
            """).SingleOrDefaultAsync(ct);
        if (!isRestricted)
            throw new InvalidOperationException(
                "PostgreSqlRuntimeRoleNotRestricted: runtime-only startup " +
                "requires an independently provisioned non-owner, " +
                "non-superuser NOINHERIT login with no role memberships, " +
                "unchanged session identity and " +
                "three exact enabled F-061 review DML triggers, " +
                "and no access to connection secret references or " +
                "encrypted provider credentials, or unauthorized " +
                "writes to connection/discovery metadata or any " +
                "non-Identity user table/column, writable sequence " +
                "or executable user-schema SECURITY DEFINER routine. " +
                "Use privileged schema bootstrap " +
                "separately; never reuse its credentials for the API.");
    }
}
