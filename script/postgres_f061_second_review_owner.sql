-- #133: install a separate immutable-journal NOLOGIN owner.
-- This is a ONE-SHOT, privileged bootstrap transaction only.
-- `SET LOCAL githubmigrator.f061_review_owner = 'validated_role'`
-- is an operator selection in an already privileged session, NOT an
-- authentication token or a remotely executable SECURITY DEFINER API.
DO $f061_owner$
DECLARE
    target_name text := pg_catalog.current_setting(
        'githubmigrator.f061_review_owner', true);
    target pg_catalog.pg_roles%ROWTYPE;
    object_name text;
    owned_count integer;
BEGIN
    IF CURRENT_USER <> SESSION_USER OR NOT EXISTS (
        SELECT 1 FROM pg_catalog.pg_roles
        WHERE rolname = SESSION_USER AND rolsuper
    ) THEN
        RAISE EXCEPTION 'F-061 owner migration requires an isolated bootstrap superuser';
    END IF;
    IF target_name IS NULL OR
       target_name !~ '^[a-z][a-z0-9_]{2,62}$' OR
       target_name = SESSION_USER THEN
        RAISE EXCEPTION 'F-061 NOLOGIN owner name invalid';
    END IF;

    -- All three ordinary immutable review tables must exist BEFORE
    -- any ownership change. No implicit table/function creation here.
    SELECT count(*) INTO owned_count
    FROM pg_catalog.pg_class c
    JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'public' AND c.relkind = 'r'
      AND c.relname IN (
        'git_later_checkpoint_mirror_second_reviews',
        'git_later_checkpoint_mirror_second_review_events',
        'git_later_checkpoint_mirror_second_review_session_consumptions'
      );
    IF owned_count <> 3 OR
       pg_catalog.to_regprocedure(
           'public.git_later_checkpoint_mirror_second_review_deny()') IS NULL
    THEN
        RAISE EXCEPTION 'F-061 second-review journal schema missing';
    END IF;
    -- Require exactly the real installed BEFORE ROW I/U/D trigger
    -- linked to the deny function on each journal, not a fake name.
    SELECT count(*) INTO owned_count
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
        OR (c.relname =
                'git_later_checkpoint_mirror_second_review_session_consumptions'
         AND t.tgname = 'trg_f061v2e_second_review_session_closed')
      );
    IF owned_count <> 3 THEN
        RAISE EXCEPTION 'F-061 deny-all trigger fences incomplete';
    END IF;

    -- Idempotent only for an already SAFE dedicated owner. Never reuse
    -- a login/admin/member role or grant login permissions to owner.
    IF NOT EXISTS (
        SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = target_name
    ) THEN
        EXECUTE pg_catalog.format(
            'CREATE ROLE %I NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB ' ||
            'NOCREATEROLE NOREPLICATION NOBYPASSRLS', target_name);
    END IF;
    SELECT * INTO STRICT target FROM pg_catalog.pg_roles
    WHERE rolname = target_name;
    IF target.rolcanlogin OR target.rolinherit OR target.rolsuper OR
       target.rolcreatedb OR target.rolcreaterole OR
       target.rolreplication OR target.rolbypassrls OR
       EXISTS (
           SELECT 1 FROM pg_catalog.pg_auth_members m
           WHERE m.member = target.oid OR m.roleid = target.oid
       ) THEN
        RAISE EXCEPTION 'F-061 owner role is login, privileged or has members';
    END IF;

    -- Never touch F-061 terminal leases, generation tables, physical
    -- objects, app login grants, other schema objects or review rows.
    FOREACH object_name IN ARRAY ARRAY[
        'git_later_checkpoint_mirror_second_reviews',
        'git_later_checkpoint_mirror_second_review_events',
        'git_later_checkpoint_mirror_second_review_session_consumptions'
    ] LOOP
        EXECUTE pg_catalog.format(
            'ALTER TABLE public.%I OWNER TO %I', object_name, target_name);
        EXECUTE pg_catalog.format(
            'REVOKE ALL PRIVILEGES ON TABLE public.%I FROM PUBLIC',
            object_name);
    END LOOP;
    EXECUTE pg_catalog.format(
        'ALTER FUNCTION public.git_later_checkpoint_mirror_second_review_deny() ' ||
        'OWNER TO %I', target_name);
    REVOKE ALL PRIVILEGES ON FUNCTION
        public.git_later_checkpoint_mirror_second_review_deny()
        FROM PUBLIC;
END $f061_owner$;
