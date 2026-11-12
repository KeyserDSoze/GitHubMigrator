-- #133: scoped, idempotent runtime LOGIN grant pilot.
-- Run ONLY from a separately authenticated privileged bootstrap session.
-- Requires SET LOCAL githubmigrator.runtime_role = 'pre_created_role'
-- within the caller's transaction. NO SECURITY DEFINER function,
-- no role creation, no plaintext password, no automatic permission on
-- future objects. This statement is intentionally not installed in DB.
DO $f061_runtime_identity_grants$
DECLARE
    runtime_name text :=
        pg_catalog.current_setting('githubmigrator.runtime_role', true);
    runtime_user pg_catalog.pg_roles%ROWTYPE;
    identity_table text;
    relation_name text;
    protected_table text;
BEGIN
    IF CURRENT_USER <> SESSION_USER OR
       NOT EXISTS (
           SELECT 1 FROM pg_catalog.pg_roles
           WHERE rolname = SESSION_USER AND rolsuper
       ) THEN
        RAISE EXCEPTION 'F-061 runtime grants require isolated superuser bootstrap';
    END IF;
    IF runtime_name IS NULL OR
       runtime_name !~ '^[a-z][a-z0-9_]{2,62}$' OR
       runtime_name = SESSION_USER THEN
        RAISE EXCEPTION 'F-061 runtime role name invalid';
    END IF;

    SELECT * INTO runtime_user
    FROM pg_catalog.pg_roles
    WHERE rolname = runtime_name;
    IF NOT FOUND OR NOT runtime_user.rolcanlogin OR
       runtime_user.rolsuper OR runtime_user.rolcreatedb OR
       runtime_user.rolcreaterole OR runtime_user.rolreplication OR
       runtime_user.rolbypassrls OR runtime_user.rolinherit OR
       EXISTS (
           SELECT 1 FROM pg_catalog.pg_auth_members m
           WHERE m.member = runtime_user.oid
       ) OR
       pg_catalog.has_database_privilege(
           runtime_name, CURRENT_DATABASE(), 'CREATE') OR
       pg_catalog.has_schema_privilege(
           runtime_name, 'public', 'CREATE') THEN
        RAISE EXCEPTION 'F-061 runtime role has excessive inherited/DDL privileges';
    END IF;

    -- An allowlisted Identity pilot MUST NOT run before all three
    -- immutable second-human review tables and their exactly scoped,
    -- enabled DENY-ALL trigger function have been installed.
    IF (
        SELECT count(*)
        FROM pg_catalog.pg_class c
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'public' AND c.relkind = 'r'
          AND c.relname IN (
              'git_later_checkpoint_mirror_second_reviews',
              'git_later_checkpoint_mirror_second_review_events',
              'git_later_checkpoint_mirror_second_review_session_consumptions'
          )
    ) <> 3 OR (
        SELECT count(*)
        FROM pg_catalog.pg_trigger t
        JOIN pg_catalog.pg_class c ON c.oid = t.tgrelid
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'public' AND NOT t.tgisinternal
          AND t.tgenabled IN ('O', 'A')
          AND (t.tgtype::integer & 31) = 31
          AND t.tgfoid =
              'public.git_later_checkpoint_mirror_second_review_deny()'::regprocedure
          AND (
              (c.relname = 'git_later_checkpoint_mirror_second_reviews'
               AND t.tgname = 'trg_f061v2e_second_review_closed')
              OR (c.relname = 'git_later_checkpoint_mirror_second_review_events'
               AND t.tgname = 'trg_f061v2e_second_review_event_closed')
              OR (c.relname = 'git_later_checkpoint_mirror_second_review_session_consumptions'
               AND t.tgname = 'trg_f061v2e_second_review_session_closed')
          )
    ) <> 3 THEN
        RAISE EXCEPTION 'F-061 independent-review journal fences are incomplete';
    END IF;

    FOREACH protected_table IN ARRAY ARRAY[
        'git_later_checkpoint_mirror_second_reviews',
        'git_later_checkpoint_mirror_second_review_events',
        'git_later_checkpoint_mirror_second_review_session_consumptions'
    ] LOOP
        IF pg_catalog.has_table_privilege(
            runtime_name, pg_catalog.format('public.%I', protected_table),
            'INSERT, UPDATE, DELETE') THEN
            RAISE EXCEPTION 'F-061 protected journal grants already excessive';
        END IF;
    END LOOP;

    -- Explicit, *existing* Identity relations only. No blanket grant
    -- on public schema objects, all tables, future tables or sequences.
    -- A separate reviewed migration is required for other API writes.
    FOREACH identity_table IN ARRAY ARRAY[
        'AspNetUsers', 'AspNetRoles', 'AspNetUserRoles',
        'AspNetUserClaims', 'AspNetUserLogins',
        'AspNetUserTokens', 'AspNetRoleClaims'
    ] LOOP
        relation_name := pg_catalog.format('public.%I', identity_table);
        IF pg_catalog.to_regclass(relation_name) IS NULL THEN
            RAISE EXCEPTION 'Required Identity relation absent';
        END IF;
    END LOOP;
    EXECUTE pg_catalog.format(
        'GRANT CONNECT ON DATABASE %I TO %I',
        CURRENT_DATABASE(), runtime_name);
    EXECUTE pg_catalog.format(
        'GRANT USAGE ON SCHEMA public TO %I', runtime_name);
    FOREACH identity_table IN ARRAY ARRAY[
        'AspNetUsers', 'AspNetRoles', 'AspNetUserRoles',
        'AspNetUserClaims', 'AspNetUserLogins',
        'AspNetUserTokens', 'AspNetRoleClaims'
    ] LOOP
        EXECUTE pg_catalog.format(
            'GRANT SELECT ON TABLE public.%I TO %I',
            identity_table, runtime_name);
    END LOOP;
    -- Real login/password rotation can update account lockout,
    -- password hash and security stamp. No general DELETE/CREATE
    -- permissions are granted by this pilot.
    EXECUTE pg_catalog.format(
        'GRANT UPDATE ON TABLE public."AspNetUsers" TO %I',
        runtime_name);

    -- One additional, strictly read-only API use-case:
    -- admin GET /api/connections. No PAT is stored in this relation;
    -- SecretReference is an internal pointer, omitted from HTTP DTO.
    -- No credential-store read, new connection, verification, rotation,
    -- migration worker or remote provider operations are authorized.
    IF pg_catalog.to_regclass('public.migration_connections') IS NULL THEN
        RAISE EXCEPTION 'Required migration_connections relation absent';
    END IF;
    -- A previous explicit GRANT SELECT(secret_reference) can live
    -- in pg_attribute.attacl, independently of table-wide relacl.
    -- Reject it BEFORE attempting any REVOKE/GRANT normalization: the
    -- subsequent effective-privilege check alone cannot prove what
    -- was installed before this isolated operator transaction.
    -- 0::oid is PUBLIC; both direct recipient and PUBLIC grants
    -- violate the read-only inventory policy even if a later
    -- REVOKE would happen to clear them.
    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_attribute a
        CROSS JOIN LATERAL pg_catalog.aclexplode(a.attacl) ace
        WHERE a.attrelid =
            'public.migration_connections'::pg_catalog.regclass
          AND a.attname = 'secret_reference'
          AND NOT a.attisdropped
          AND ace.privilege_type = 'SELECT'
          AND ace.grantee IN (runtime_user.oid, 0::oid)
    ) THEN
        RAISE EXCEPTION
            'F-061 runtime grant pilot detected existing secret or write privileges';
    END IF;

    -- Column-level permission excludes secret_reference even from
    -- direct SQL by the restricted login (not merely from HTTP JSON).
    -- Revoke the previously issued table-wide pilot SELECT before
    -- applying least privilege. Repeated executions are idempotent.
    EXECUTE pg_catalog.format(
        'REVOKE SELECT ON TABLE public.migration_connections FROM %I',
        runtime_name);
    EXECUTE pg_catalog.format(
        'GRANT SELECT (id, name, provider_key, direction, organization, ' ||
        'created_at_utc, validated_at_utc, validation_code, authenticated_as, ' ||
        'permission_assessment) ON TABLE public.migration_connections TO %I',
        runtime_name);

    -- Read-only source discovery inventory is allowed for authenticated
    -- existing admin GET /api/discoveries and GET /{id} endpoints. The
    -- discovery domain contract excludes PAT/cookies/credential URLs.
    -- No grant on discovery checkpoints, unreviewed observations,
    -- encrypted secrets, write jobs, F-061 journals or worker leases.
    IF pg_catalog.to_regclass('public.migration_discoveries') IS NULL THEN
        RAISE EXCEPTION 'Required migration_discoveries relation absent';
    END IF;
    EXECUTE pg_catalog.format(
        'GRANT SELECT ON TABLE public.migration_discoveries TO %I',
        runtime_name);
    -- Verify the EFFECTIVE permissions after all grants/revokes,
    -- including pre-existing column-specific and PUBLIC privileges.
    -- Table-level REVOKE SELECT alone does not remove a separate
    -- GRANT SELECT(secret_reference) from an earlier deployment.
    -- Fail the whole operator transaction instead of silently
    -- accepting a runtime credential able to inspect secret keys.
    IF pg_catalog.has_column_privilege(
           runtime_name, 'public.migration_connections',
           'secret_reference', 'SELECT')
       OR pg_catalog.has_table_privilege(
           runtime_name, 'public.migration_connection_secrets',
           'SELECT, INSERT, UPDATE, DELETE')
       OR pg_catalog.has_table_privilege(
           runtime_name, 'public.migration_connections',
           'INSERT, UPDATE, DELETE')
       OR pg_catalog.has_table_privilege(
           runtime_name, 'public.migration_discoveries',
           'INSERT, UPDATE, DELETE')
    THEN
        RAISE EXCEPTION 'F-061 runtime grant pilot detected existing secret or write privileges';
    END IF;
    -- Fail closed on pre-existing DML elsewhere in the runtime DB.
    -- A GRANT UPDATE(repositories_json), unlike table-wide UPDATE,
    -- otherwise escapes has_table_privilege checks. Do not normalize
    -- dangerous pre-existing access: abort the grant transaction.
    -- The narrowly scoped AspNetUsers UPDATE is the ONLY permitted
    -- writable user relation in this read-only pilot.
    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_class writable
        JOIN pg_catalog.pg_namespace ns
          ON ns.oid = writable.relnamespace
        WHERE writable.relkind IN ('r', 'p', 'v', 'f')
          AND ns.nspname <> 'information_schema'
          AND pg_catalog.left(ns.nspname, 3) <> 'pg_'
          AND (
              (
                  ns.nspname = 'public'
                  AND writable.relname = 'AspNetUsers'
                  AND (
                      pg_catalog.has_table_privilege(
                          runtime_name, writable.oid, 'INSERT, DELETE')
                      OR EXISTS (
                          SELECT 1 FROM pg_catalog.pg_attribute a
                          WHERE a.attrelid = writable.oid
                            AND a.attnum > 0 AND NOT a.attisdropped
                            AND pg_catalog.has_column_privilege(
                                runtime_name, writable.oid, a.attname, 'INSERT')
                      )
                  )
              )
              OR (
                  NOT (ns.nspname = 'public' AND
                       writable.relname = 'AspNetUsers')
                  AND (
                      pg_catalog.has_table_privilege(
                          runtime_name, writable.oid, 'INSERT, UPDATE, DELETE')
                      OR EXISTS (
                          SELECT 1 FROM pg_catalog.pg_attribute a
                          WHERE a.attrelid = writable.oid
                            AND a.attnum > 0 AND NOT a.attisdropped
                            AND pg_catalog.has_column_privilege(
                                runtime_name, writable.oid,
                                a.attname, 'INSERT, UPDATE')
                      )
                  )
              )
          )
    ) THEN
        RAISE EXCEPTION
            'F-061 runtime grant pilot detected existing secret or write privileges';
    END IF;
END $f061_runtime_identity_grants$;
