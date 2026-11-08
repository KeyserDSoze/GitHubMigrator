# F-087 — acquire independent F-054 and F-059/F-058 remote evidence after F-082

## What is now operational

F-086 persistently captures the original typed GitHub head/tag OIDs and the
CREATE-ONLY ref list before F-082's possible-send marker. F-087 gives an
independent human a **manual, read-only acquisition** from exactly those
historical inputs. The operation is safe to start after a crash and after
the original F-052/F-050 review deadlines, without re-executing the sender.

The two separate stages are:

1. **Observe tips:** Re-read F-086 with F-082 committed, mint one ephemeral
   single-repository GitHub App **Contents:read** token, call F-053 GitHub
   Git Data REST GETs, recheck F-054's complete original ref+candidate
   and tip digest, then use a dedicated F-087 F-054 journal to record an
   immutable exact or mismatched observation.
2. **Verify full objects:** Requires an existing exact F-054 observation
   by a different independent actor. Mint another ephemeral
   **Contents:read** token and invoke existing F-059 trusted plan reader,
   F-060 local source bundle materialization, GitHub smart HTTPS
   **upload-pack (read-only fetch)**, F-056 full reachable-object audit
   and F-058 append-only reconciliation. The original tag-object types and
   CREATE-ONLY OIDs are retrieved exclusively from F-086. Existing F-059
   checks the source snapshot and physical mirror again before persisting
   proof. An F-085 checkpoint can subsequently be sealed by a further
   independent superadmin.

F-083's sender code, sandbox switches and writer are **never injected**
into this coordinator. F-087 never calls `git push`. Both result types
permanently report `CanPush=false`, `CanRetry=false`,
`MigrationComplete=false`. It never treats successful GETs as evidence
that the previously executed sender caused remote changes.

## API and configuration

Both routes are **POST without request body** because they actively
perform read-only I/O; they are protected with PasswordChanged +
superadmin authorization and the existing dispatch-confirmation rate
limit. No URL, token, ref name/OID or arbitrary refspec is accepted.

- `POST /api/incremental-preparations/{id}/operation-plans/{planId}/github-app-scopes/{scopeId}/remote-sandbox-review-sessions/{sessionId}/dispatch-intents/{intentId}/confirmations/{confirmationId}/independent-recovery/{barrierId}/observe-tips`
- `POST <same>/verify-full-objects`

This path is disabled by default. An operator must explicitly set
`GitHubIndependentRecovery:Enabled` and its exact
`AllowedRepositoryId` and `AllowedRepository`; the distinct F-086
and F-054/F-058 allowlists and F-059 source/mirror controls must
independently permit the same sandbox repository. The separate
`GitHubIndependentRecovery:ReadCredentialsEnabled` switch (false by default)
and `AppId`, `InstallationId`, `PrivateKeySecretReference` settings
select the recovery-only GitHub App installation credential. F-043's
WRITE-token preflight switch does NOT have to be enabled for recovery.
The private key remains in the secure secret store.

The token minting adapter uses exactly `repositories=[repo]` and
`permissions={contents:read}` with implicit Metadata:read; it checks
the installation owner, numeric repository ID and actual token
permissions before providing a token **only within a callback**. No
token is stored in database tables, DTOs, log text or Git command args.

## PostgreSQL recovery-specific F-054

The historical F-054 journal is unchanged for pre-send observations.
F-087 adds `PostgresGitHubRemoteRecoveredReadbackJournal` with an
additional F-082/F-086 provenance guard:

- Recompute the F-054 typed observation/tip assessment digest.
- Acquire the repo advisory locks in order **732911 → 733050**.
- Re-read the original immutable F-086 snapshot and the F-082/F-055
  hazard and audit chain **inside the transaction**.
- Verify all original F-052 grant, F-050 fence and F-047 blocked attempt
  hashes and permanent no-transport flags, plus a distinct observer
  from the original sender and human reviewers.
- Treat expiration and `RecoveryRequired` as *historical evidence
  context*, never as execution authority. Old leases or grants cannot
  authorize another push.
- Store a single immutable F-054 outcome and event. Same observer +
  same SHA is idempotent; conflicting evidence cannot overwrite it.
- An unexpected remote ref produces `OperatorReviewRequired` and
  blocks F-059. If GitHub fails before the GET proof can be sealed,
  the F-085 journal remains in `AwaitingIndependentReadback`.
- The full-object stage cannot start unless F-054 is exact and the
  second observer differs from the first. F-059 again revalidates the
  physical source, provider refs and PostgreSQL grant/hazard lineage.

## Verification

- Domain tests with fake token scope/provider and injected F-054/F-059
  validate execution order, durable evidence reuse, token stays
  ephemeral, distinct operators and mismatch/ref drift blocked.
- Real PostgreSQL 17 integration seeds the complete
  F-081→F-086→F-082 lineage, expires the original grant/fence, then
  records F-054 from an independent F-087 observer and sees F-085 move
  from pending to tips-observed after a fresh DB connection.
- Additional PG cases cover mismatched remote refs, duplicate or
  conflicting observers and failure to observe without an F-082 mark.
- API JWT tests prove disabled-by-default and superadmin-only access
  with no provider calls.
- Existing F-059, F-057 and F-058 suites continue to exercise actual
  Git smart fetch against SHA-1/SHA-256 bare remotes, exact source
  mirroring and canonical closure persistence.

**Boundary:** CI deliberately makes no real GitHub.com push and no
GitHub.com credential mint. Only the mock and local Git test adapters
are exercised in CI. Production activation requires explicit sandbox
configuration and an independent approval procedure; no background
retry or sender worker is introduced. F-087 is stacked on F-086,
with `main` unchanged.
