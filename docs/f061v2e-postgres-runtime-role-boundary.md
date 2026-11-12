# F-061v2e / #133 — PostgreSQL runtime-only role boundary

This is a **default-off, fail-closed startup path**, not a deployed least-privilege migration and **not** approval to write a second-review journal. Existing deployments continue their old bootstrap behavior until an operator explicitly provisions separate role credentials and enables the opt-in mode.

## Two independent phases

**Privileged one-shot bootstrap phase** (`PostgreSql__BootstrapOnly=true`, `PostgreSql__RuntimeOnly=false`): runs the real `Bootstrap.InitializeAsync` and **all** existing ordered `*Schema.InitializeAsync` calls with a separately controlled privileged migration credential. After the final schema initialization succeeds, the process **exits before mapping or opening HTTP routes**, so the bootstrap credential is not used for a long-running API. On schema failure the process fails instead of serving partial state. The prior combined bootstrap-and-serve mode remains the compatibility default only when both options are `false`; do not treat that legacy mode as an isolated production security boundary.

**Mutual exclusion:** `PostgreSql__BootstrapOnly=true` together with `PostgreSql__RuntimeOnly=true` is rejected immediately as `PostgreSqlStartupModeConflict`, before database access. Test coverage checks every combination.

**One-shot operator invocation:** supply the trusted bootstrap connection string, signing key and existing bootstrap configuration **through a protected process environment / secrets manager**, not literal shell command history. Invoke the same built API entrypoint with `PostgreSql__BootstrapOnly=true` and wait for successful process exit; do not expose a public port. Re-run the existing idempotent schema setup when needed, then fully stop the privileged process. The deployment orchestration (including credential injection, success status and account provisioning) is still a separate operational responsibility.

**Runtime phase** (`PostgreSql__RuntimeOnly=true` in environment): skips **all** in-process Identity seeding, EF schema creation/repair and migration initializers. Instead, startup runs `PostgresRestrictedRuntimeRoleGuard.RequireSafeAsync` on the actual `AppDbContext` connection, before `app.Run()`. It rejects the process with `PostgreSqlRuntimeRoleNotRestricted` if PostgreSQL reports:

- the login is a superuser, can create roles/databases, replicate or bypass RLS;
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
