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
END $f061_runtime_identity_grants$;
