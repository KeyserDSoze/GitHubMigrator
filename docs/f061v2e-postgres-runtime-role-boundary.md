# F-061v2e / #133 — PostgreSQL runtime-only role boundary

This is a **default-off, fail-closed startup path**, not a deployed least-privilege migration and **not** approval to write a second-review journal. Existing deployments continue their old bootstrap behavior until an operator explicitly provisions separate role credentials and enables the opt-in mode.

## Two independent phases

**Privileged one-shot bootstrap phase** (`PostgreSql__BootstrapOnly=true`, `PostgreSql__RuntimeOnly=false`): runs the real `Bootstrap.InitializeAsync` and **all** existing ordered `*Schema.InitializeAsync` calls with a separately controlled privileged migration credential. After the final schema initialization succeeds, the process **exits before mapping or opening HTTP routes**, so the bootstrap credential is not used for a long-running API. On schema failure the process fails instead of serving partial state. The prior combined bootstrap-and-serve mode remains the compatibility default only when both options are `false`; do not treat that legacy mode as an isolated production security boundary.

**Mutual exclusion:** `PostgreSql__BootstrapOnly=true` together with `PostgreSql__RuntimeOnly=true` is rejected immediately as `PostgreSqlStartupModeConflict`, before database access. Test coverage checks every combination.

**One-shot operator invocation:** supply the trusted bootstrap connection string, signing key and existing bootstrap configuration **through a protected process environment / secrets manager**, not literal shell command history. Invoke the same built API entrypoint with `PostgreSql__BootstrapOnly=true` and wait for successful process exit; do not expose a public port. Re-run the existing idempotent schema setup when needed, then fully stop the privileged process. The deployment orchestration (including credential injection, success status and account provisioning) is still a separate operational responsibility.

**Runtime phase** (`PostgreSql__RuntimeOnly=true` in environment): skips **all** in-process Identity seeding, EF schema creation/repair and migration initializers. Instead, startup runs `PostgresRestrictedRuntimeRoleGuard.RequireSafeAsync` on the actual `AppDbContext` connection, before `app.Run()`. It rejects the process with `PostgreSqlRuntimeRoleNotRestricted` if PostgreSQL reports:

- the login is a superuser, can create roles/databases, replicate or bypass RLS;
- the login has INHERIT enabled, is not a LOGIN role, or has ANY role membership that could be activated with `SET ROLE`;
- ownership/membership of the database, any F-061 second-review journal table or its deny trigger function;
- `CREATE` permission on the database or the `public` schema;
- any `INSERT`, `UPDATE` or `DELETE` privilege on the three F-061 second-review tables;
- missing F-061 review/event/session tables or a missing/disabled DENY-ALL trigger;
- an impersonated `CURRENT_USER` that differs from the authenticated `SESSION_USER` (e.g. `SET ROLE` from a superuser session);
- a same-named trigger redirected to another function or lacking a row-level BEFORE INSERT/UPDATE/DELETE event mask.

The guard is a startup precondition, **not** a defense against an administrator
who controls the database or a superuser credential. Keep privileged
bootstrap secrets entirely out of the runtime process; `SET ROLE` is
not a substitute for separate authentication.

The guard queries PostgreSQL system catalogs and effective privileges—not user-supplied headers, claims, custom GUCs or cached role names. It does **not** grant normal application CRUD privileges to the restricted login and does not promise that every endpoint is functional. An operator still needs an audited non-superuser role provisioning and grants migration before switching an actual deployment. The runtime account must have only independently reviewed permissions on the *other* application tables, sequences and Identity relations; blanket grants on the F-061 journal tables are forbidden.

## Security and rollout

- Do **not** set `RuntimeOnly=true` with the superuser password and assume the API will start: it must refuse. Set `BootstrapOnly=true` **only** in an isolated one-shot process, never in the long-lived API container. Both flags together fail immediately. Never supply the bootstrap credential to an untrusted runtime process.
- Provision and verify a distinct login via an independently controlled privileged migration operation; keep any journal writer role inaccessible until the separate #130 source-first SQL transaction, authenticated second-human decision and POSIX proof are fully tested.
- A future writer must not rely on `SET ROLE`, a caller-set `agic.*` configuration variable or a broad `SECURITY DEFINER` function grant. The existing mutation-denial triggers stay installed and enabled.
- In CI, the separate-login PostgreSQL17 test verifies that the superuser fails the opt-in guard while a distinct restricted principal passes the catalog guard, and that raw SQL inserts/updates/deletes, trigger disabling, `SET ROLE`, forged GUC values and journal event/session inserts are still denied. No filesystem, F-061 generation, remote GitHub push or migration-complete authority is introduced.

**Remaining #133 work:** a reviewed and repeatable schema-owner/bootstrap-role provisioning process, exact least-privilege grants for all existing API relations, genuine runtime end-to-end API tests under that role, and full final-main 4/4 CI verification. One-shot mode separates the *process lifetime* but does not itself provision privileges or grants. #130 independently requires the protected atomic journal writer and concurrent-replay tests.

## #133: opt-in SQL Identity-only permission pilot

A separately provisioned PostgreSQL `LOGIN NOINHERIT NOSUPERUSER NOCREATEDB
NOCREATEROLE NOREPLICATION NOBYPASSRLS` can receive only the minimum
initial permissions required for **pre-existing Identity accounts**.
The reviewable [pilot SQL](../../script/postgres_runtime_identity_grants.sql)
makes no new role, contains no password, creates no function, and grants
neither schema CREATE nor journal DML. It permits SELECT on the seven
existing `AspNet*` Identity relations and UPDATE on `AspNetUsers` for
login/lockout/password rotation; no DELETE/INSERT or privilege on other
application tables or sequences is added. It is NOT yet a general
production runtime role for all API workflows.

After the isolated one-shot bootstrap, provision the runtime LOGIN
using an independent secret manager and then, **while authenticated as
the privileged bootstrap operator**, set
`GITHUBMIGRATOR_RUNTIME_PGROLE` to that existing lower-case role name
and execute `bash script/provision_postgres_runtime_identity.sh`.
Set the usual secure libpq environment variables/PGPASSFILE for the
privileged operator. The wrapper validates the role identifier and
passes the static SQL through `psql -X --single-transaction -v
ON_ERROR_STOP=1`. The SQL verifies the operator is a superuser
(unmodified SESSION_USER), the target role is a separate no-inherit
LOGIN with zero memberships/DDL authority and none of the three
protected F-061 journals has DML granted. It verifies three installed
DENY-ALL triggers and requires the seven Identity tables to exist.
Repeated grants are idempotent, and any failure rolls back all grants.

**No password is included in the shell argv or migration SQL.**
Never run this script with runtime application credentials, install
it as a SECURITY DEFINER function or treat a custom GUC as caller
authentication: only a privileged *one-shot administrative connection*
can execute the DO block. The `githubmigrator.runtime_role` session
setting identifies the intended grant recipient within that trusted
transaction, not a review-writer authentication mechanism.

The PG17 integration test executes exactly this SQL twice for an
ephemeral independently authenticated login, verifies real Identity
SELECT/UPDATE, that DELETE, schema CREATE and all three protected
journal INSERTs remain forbidden, and checks the restrictive runtime
startup guard. Future work must grant explicitly reviewed DML on other
API relations and verify end-to-end HTTP functionality under a
separately authenticated runtime-only host. **No writer for #130 is
enabled**.

## Verified real runtime HTTP host (PG17)

The current CI regression now starts a **second real ASP.NET Core host**
with `PostgreSql__RuntimeOnly=true`, using the separately authenticated
ephemeral LOGIN after the exact Identity-only grants have been applied.
It verifies `/api/health`, `/api/ready` and a real
`POST /api/auth/login` for the pre-existing superadmin account,
including issuance of a JWT with the correct role. The privileged
bootstrap test host is used only to create the account and to provision
the disposable LOGIN. The restricted host does not run schema/Identity
bootstrap and its database credentials are restored immediately after
the request. A further real PG17 assertion refuses a runtime LOGIN
that has any role membership (even a NOLOGIN group without grants),
so `NOINHERIT` cannot conceal later `SET ROLE` escalation.

This proves only the initial **Identity/authentication HTTP slice**.
It does not grant or test general source discovery, plan editing, source
checkpoint or GitHub migration CRUD in a restricted application role;
all three F-061 review journals still deny mutation. #130 remains
independently blocked until an authenticated, atomic three-row writer.

## Explicit admin connection listing (read only)

The same reviewed pilot SQL now grants **column-level SELECT only**
on the explicitly allowlisted public metadata fields of
`public.migration_connections`, in addition to Identity SELECT/UPDATE.
It revokes any earlier table-wide SELECT pilot grant on that relation
before issuing the narrowed permissions. In particular,
`secret_reference` cannot be retrieved **even through direct SQL**
as the runtime login; `PostgresConnectionRepository.ListAsync` now
projects only those same allowlisted columns. This supports the existing, authenticated
`GET /api/connections` admin/superadmin route. The API's response DTO
omits `secret_reference`, and the connection row contains a *reference*,
not the provider PAT (which resides in a separate encrypted store).
The restricted PostgreSQL login intentionally has no SELECT or write
grant on the encrypted credential table, and no INSERT/UPDATE/DELETE on
`migration_connections`; `POST /api/connections`, credential
rotation, verification and removal remain outside this grant pilot.

The PostgreSQL17 integration test checks real SQL SELECT and negative DML
permissions, then logs in to an independently restricted ASP.NET host,
checks anonymous `GET /api/connections` is 401 and authenticated
`GET /api/connections` returns the two existing test connection IDs
without raw source/target tokens or `secretReference` values. No remote
provider calls or migration execution are part of this read-only scope.

## #133: dedicated NOLOGIN owner for immutable F-061 second reviews

The optional [operator SQL](../../script/postgres_f061_second_review_owner.sql)
and [one-shot wrapper](../../script/provision_postgres_f061_second_review_owner.sh)
now support an **independent, passwordless NOLOGIN owner** for precisely the
three second-review, event, and one-use session consumption tables and
their shared DENY-ALL trigger function. This is not a runtime login or
the future decision writer. It must run after the privileged schema
bootstrap, on the trusted superuser session, and before switching the
API to `PostgreSql__RuntimeOnly=true`.

The operator supplies `GITHUBMIGRATOR_F061_OWNER_ROLE` (safe lower-case
PostgreSQL role name) and the protected libpq bootstrap connection.
The wrapper executes `psql -X --single-transaction`; the SQL checks
`CURRENT_USER=SESSION_USER` and `rolsuper`, the exact three existing
ordinary tables and enabled row-level BEFORE INSERT/UPDATE/DELETE
triggers bound to the real deny function, then creates the NOLOGIN
NOINHERIT role if absent or verifies that a preexisting role has no
superuser, DDL, replication or RLS-bypass powers and no incoming or
outgoing memberships. Only then does it atomically transfer the
three table owners and deny-function owner and revoke all
`PUBLIC` table and trigger-function privileges. Repeated runs are
idempotent. It does **not** transfer ownership of the original
F-061 terminal lease or any other app/Identity source table.

The new real PG17 test executes the exact SQL **twice** for a disposable
NOLOGIN owner, confirms all three table owners and the deny-function
owner, checks a separately authenticated NOSUPERUSER LOGIN still passes
the restricted-role guard but is refused direct journal INSERT and
`SET ROLE` to the protected owner, then restores the prior object
owners and drops test-only roles. No producer of second-human review
decisions is installed; no new immutable generation is minted.

**Caution:** `ALTER OWNER` is an administrative schema mutation.
Run as an isolated one-shot migration with a fresh database backup,
and review bootstrap/DDL reapplication across deployments.
The runtime-only login must never possess this owner role, its
credentials or membership. Additional per-relation application grants
and full restricted HTTP CRUD verification remain in #133.

### PostgreSQL SQL-level credential-reference regression

The real PG17 restricted-login test asserts that the role has NO
table-wide `migration_connections` SELECT, can SELECT the specifically
granted `name` column, cannot SELECT the `secret_reference` column
(`42501`), and still receives the authorized, sanitised HTTP
`GET /api/connections` list. The original full-record `GetAsync`
path deliberately requires a different, separately reviewed privilege;
this scoped pilot does not grant access to validation, verification,
token rotation, creation or deletion APIs. No encrypted secret payload
or lookup reference reaches the restricted SQL session.

## Read-only immutable source discovery on the restricted host

The separate SQL-role pilot now grants `SELECT` on the existing
`public.migration_discoveries` relation, to support existing admin-only
`GET /api/discoveries` and `GET /api/discoveries/{id}`. The canonical
discovery-domain contract contains no PAT, cookie, authorization header,
credential URL or secret reference. The PostgreSQL login receives no
`INSERT/UPDATE/DELETE` on that relation and no grants on worker jobs,
checkpoint pages, encrypted credential storage, F-061 journal tables,
source materialization or outbound GitHub APIs. In PG17 an independently
authenticated restricted runtime host reads an immutable, bootstrap-seeded
discovery snapshot by its ID, verifies source ID/JSON consistency and
checks unauthenticated listing is HTTP 401. Direct SQL INSERT is denied
with 42501; creating/discovering new inventories remains out of scope.

## #132: fail-fast real PG17 role-boundary smoke before the entire gate

GitHub Actions now runs the three existing restricted-role, isolated
NOLOGIN-owner and HTTP-auth integration test classes **first**, on the
same real PostgreSQL17 service, producing its own `pg17-runtime-role-smoke.trx`.
If a new permission mistake fails, the pipeline fails before the
~55-minute Git physical ancestry theories. **After smoke success**, the
original `dotnet test` command still runs *all* integration cases,
including every SHA1/SHA256 scenario, with its own complete TRX and
code coverage. This reduces time-to-feedback for regressions without
dropping or shortening any accepted test. It does not yet reduce the
wall-clock of a successful full PG17 job.

## Fail-closed operator grant audit of pre-existing privileges

The scoped operator SQL now checks the **effective** grant state at the end
of its transaction, not just the statements it attempted to execute.
In particular, revoking table-level `SELECT` does **not** remove a
separately granted `SELECT(secret_reference)`: the script rejects that
pre-existing privilege. It also refuses any effective read/write access
to `migration_connection_secrets`, or existing DML on either
`migration_connections` or `migration_discoveries`. Because this
attestation runs inside the same psql `--single-transaction`, any
unsafe state aborts all attempted new grants instead of silently
normalizing or expanding privileges. The operator must separately audit
and revoke the excessive existing privileges before retrying.

The real PostgreSQL17 smoke regression now grants each forbidden
`secret_reference`-column and encrypted-store SELECT privilege
temporarily to its disposable runtime LOGIN, asserts that running the
**exact unchanged operator SQL** raises an exception, revokes each
temporary permission and checks the ordinary independent runtime/API
smoke remains valid. This operator check applies to the intentionally
narrow **read-only pilot**, not the eventual full application role with
separately reviewed business write capabilities. No privilege or
F-061 journal mutation is introduced by this verification.

## Fail-closed runtime startup against post-provisioning privilege drift

The `PostgresRestrictedRuntimeRoleGuard` checks real PostgreSQL
catalog **effective privileges on each `RuntimeOnly` startup**, not
just the initial grants operator script. It refuses a runtime LOGIN if
`migration_connections.secret_reference` is SELECT-readable (including
column-level grants), if the encrypted
`migration_connection_secrets` store permits any SELECT/INSERT/UPDATE/
DELETE, or if either required secret-related relation is absent.
No role switch, bootstrap-superuser credential, owner membership or
disabled F-061 review journal trigger is allowed.

A real PostgreSQL17 integration regression now first provisions the
restricted LOGIN correctly, then temporarily grants each forbidden
secret-reference and encrypted-store SELECT **after** provisioning.
The actual startup guard must refuse both even though the account
otherwise passes every role and journal-fence check. Each temporary
grant is revoked, and only then may the independent restricted API host
start and complete the normal authenticated HTTP tests.

This is a **start-time validation**, not continuous PostgreSQL grant
monitoring. Administrators must prevent role/ACL changes while an API
process is running and rotate/restart on credential or grant changes.
It does not enable #130's three-row second-human writer.

## #133: metadata/discovery DML grant drift blocks runtime startup

In the current **read-only** pilot the runtime guard now requires that
`migration_discoveries` exists and that the connected runtime LOGIN
has **no effective INSERT, UPDATE or DELETE** on either
`migration_connections` or `migration_discoveries`. This covers
unauthorized post-provisioning role grants that could overwrite
connection metadata or an immutable source inventory, even if the
operator script originally completed successfully.

The independent real PG17 smoke test grants `UPDATE` temporarily
to each table **after** the approved Identity/connection/discovery
permissions are installed, checks that the actual runtime-only
startup guard refuses both, then revokes the test-only grants and
proves the restricted login can proceed to authenticated read-only
HTTP requests. None of the actual application metadata or discovery
rows is modified, and F-061 second-review journals remain deny-all.

Any future write-capable application role must have a separately
reviewed owner/grant policy and an authorization redesign; this
pilot's intentionally strict runtime-only guard must not be bypassed
simply to enable additional CRUD endpoints.

## #133: global write-privilege fence for the current read-only pilot

The runtime-only login is not allowed arbitrary DML outside the
original Identity `AspNetUsers` UPDATE grant. The startup guard and
the separately executed operator grant SQL now scan the real PostgreSQL
catalog of **non-system user tables, views, and foreign tables** for
effective INSERT/UPDATE/DELETE. They check both **table** privileges
and **individual-column INSERT/UPDATE grants**, which table-level
`has_table_privilege` does not detect on its own. On `AspNetUsers`
itself, UPDATE remains permitted but INSERT/DELETE are rejected.

This blocks privilege drift even on unrelated application tables such
as `migration_plan_drafts`. The real PostgreSQL17 smoke temporarily
grants full-table UPDATE and then column-only UPDATE of
`migration_plan_drafts.repositories_json`, plus DELETE on
`AspNetUsers`, and requires the runtime startup to refuse each
before restoring grants. No sample plan draft or Identity account is
modified. The operator script rejects comparable pre-existing
privileges in its own atomic installation transaction.

The restriction is **specific to the current read-only deployment
pilot**. It is not a complete privilege policy for a future
write-enabled migration API. Such a role must be designed and tested
separately rather than weakening the fail-closed checks or using the
bootstrap superuser as an ordinary runtime credential.

## #134: TRUNCATE, TRIGGER and sequence-write privilege drift

The default-OFF PostgreSql__RuntimeOnly read-only pilot must also reject
SQL permissions that are not covered by INSERT/UPDATE/DELETE checks.
PostgreSQL TRUNCATE does not fire row-level DELETE triggers, so granting
it on a protected F-061 journal would defeat the intention of a
DELETE-only permission scan. Table-level TRIGGER permissions also
provide an independent way to modify table behavior. The runtime guard
and one-shot privileged grants SQL therefore fail closed on either
effective privilege on any non-system user relation, including
the otherwise UPDATE-allowlisted Identity AspNetUsers table.

A second catalog scan refuses sequence USAGE or UPDATE: those permit
nextval/setval without table DML grants. The current restricted login
requires neither permission. Real PostgreSQL17 integration smoke now
temporarily grants both table privileges (including F-061 and Identity)
and sequence privileges to a disposable independently authenticated
LOGIN. It requires both the production runtime-only guard and
the exact operator SQL transaction to reject each grant, before revoking
it and continuing with the ordinary HTTP smoke. No application row is
truncated, no sequence value is advanced, no deny-all trigger is changed,
and no #130 writer or recovery authority is enabled.

These fences define only this narrowly scoped read-only runtime role.
A future write-capable role needs an independently reviewed permission
model; a complete final-main 4/4 CI result is still required for #134.


### #134 PG17 smoke correction: sequence catalog predicate evaluation

The first real PostgreSQL17 CI smoke after introducing sequence privilege
checks (run 43249592069) failed SQLSTATE **42809**, because PostgreSQL
can evaluate `has_sequence_privilege` for a non-sequence TOAST catalog
row even when an independent `WHERE relkind = 'S'` predicate appears
first. Both the runtime-only SQL guard and the one-shot operator script
now use an explicit `CASE WHEN relkind = 'S' ... THEN
has_sequence_privilege(...) ELSE false END` barrier, rather than
relying on unspecified predicate evaluation order. This does not relax
USAGE/UPDATE denial or any protected F-061 table trigger. The existing
real-PG17 separated-role smoke must prove this on the final main commit;
a canceled or previously failed job is not acceptance.

### #135: unrestricted CREATE on another user schema is forbidden

The restricted `PostgreSql__RuntimeOnly=true` SQL pilot must refuse
**effective `CREATE` on every non-system schema**, not merely `public`:
a separately granted schema privilege permits DDL even without
`CREATE` on the database. Both the startup catalog guard and the
one-shot privileged grant script inspect `pg_namespace` and effective
`has_schema_privilege(role, schema_oid, 'CREATE')` for all user schemas.
This also catches rights granted to `PUBLIC` and privilege drift
*after* provisioning. No migration-object CREATE, ownership, or
`SECURITY DEFINER` privilege is granted.

The real PG17 regression creates a disposable additional schema and
checks direct-runtime and `PUBLIC` CREATE drift against both codepaths;
it revokes and drops test-only objects. All existing F-061 review
DENY-ALL triggers, no-writer setting, Identity-only UPDATE exception
and destination-send authority flags remain unchanged. This is a
default-off pilot; **not** production migration CRUD provisioning.

### #136: refuse MAINTAIN and REFERENCES even without table DML

In PostgreSQL17, a role with effective `MAINTAIN` on a relation can
execute `VACUUM`, `ANALYZE`, `CLUSTER`, `REINDEX`,
`REFRESH MATERIALIZED VIEW` and `LOCK TABLE`. Separately, a
`REFERENCES` privilege can support foreign-key definitions.
The restricted default-OFF Identity/discovery runtime pilot requires
neither capability. Both the actual startup SQL role guard and the
one-shot operator SQL now refuse **effective `MAINTAIN` and
`REFERENCES` on any user relation**, including Identity and the three
DENY-ALL protected second-human journals, while keeping all existing
DML/TRUNCATE/TRIGGER/sequence/CREATE/secret checks. This policy covers
privileges granted directly or via `PUBLIC`.

The existing real independent-LOGIN PG17 grant-drift smoke tests now
include each privilege on a read-only discovery table and a protected
journal. The test never invokes maintenance commands, creates a
foreign key or mutates journal data; every temporary grant is revoked
in a `finally` clause. The positive post-revocation restricted host
probe remains unchanged. This scope is documented under issue #136;
it **does not grant a writer, enable a remote push, or supersede**
#130/#133. Exact-main completed 4/4 CI is still required for closure.
