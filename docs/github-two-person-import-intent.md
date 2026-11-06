# F-040 — Two-person Git import intent and read-only destination policy review

## Operational purpose and hard boundary

This increment introduces a **two-person, expiring, non-authorizing Git
import intention**. It is a review artifact and NEVER a push credential,
GitHub repository creation, remote ref update, execution lease or permission
grant. PostgreSQL CHECK constraints still force
`write_capability_verified=false`, `execution_authorized=false`,
`ready_for_execution=false`. F-029/F-034 stay blocked.

The review is bound to existing F-039 import-readiness, F-032
source-vs-target exact object IDs, F-035 source bundle, F-038 physical
SHA-256 and the F-029 manifest. It is specifically **not** an import.

## Three fixed-host GitHub GET requests

The new `GitHubImportPolicyReader` uses only the application's existing
GitHub destination token, recovered from the encrypted secret store.
No caller may supply an HTTP URL, repository name, branch, PAT or refspec.

- `GET https://api.github.com/repos/{org}/{repo}`: verify exact full
  name + owner, and obtain default branch only from the GitHub response.
- `GET .../rulesets?includes_parents=true&per_page=100`: inspect
  repository rulesets with inherited rules, inspect enforcement state,
  bound response to 64 KiB, and reject incomplete pagination.
- `GET .../branches/{defaultBranch}/protection`: record whether default
  branch protection is visible. A 404 is explicitly
  `NotVisibleOrMissing`, *not* proof that no protection applies.

HTTPS `api.github.com` only, clean typed HttpClient with no
auto-redirect/cookies, status-dependent fail-closed interpretation.
Policy findings are advisory. They cannot prove token Contents:write,
Git push transport permissions, inherited ruleset bypass, fine-grained
token scopes or every branch's protection.

## Non-destructive Git ref intent

F-032's **full** paginated immutable comparison (at most 50,000
combined refs) is reloaded and validated against its SHA, counts,
manifest and target evidence. A deterministic SHA-256 hashes
each sorted ref, source SHA, target SHA and exact disposition.

- `MatchingObjectId` remains untouched.
- `TargetOnlyPreserved` remains untouched and is never deleted.
- `CreateCandidate` can only represent a *currently missing* ref
  with a valid source Git object ID.
- `DivergedBlocked` never permits any intention to be created; no
  force-push or optimistic fast-forward assertion.

The F-040 plan is data only: no executable command string, push
refspec or target URL. In F-040 a plan must have at least one safe
create candidate.

## API, roles and separation of duties

Admin/superadmin with password_changed and valid JWT can POST:

```http
POST /api/incremental-preparations/{prepId}/operation-plans/{planId}/transfer-workpacks/{workpackId}/import-readiness/{reviewId}/intents
{"expectedReadinessSha256":"<F-039 SHA-256>"}
```

Returns `202 Accepted` and a `Pending` reviewed intention. It
expires **10 minutes** after creation. The operation takes a current
F-039 review that reports a visible, enabled, unarchived repository and
advisory reported push permission. It re-hashes the real F-038 bundle
before policy GETs and again inside a PostgreSQL source-scoped advisory
transaction lock after the GETs. An untrustworthy or changed proof fails.

Only a **different superadmin** (not the requestor) can POST:

```http
POST /api/incremental-preparations/{prepId}/operation-plans/{planId}/import-intents/{intentId}/decision
{"expectedReadinessSha256":"<F-039 SHA-256>",
 "decision":"Acknowledge",
 "acknowledgementText":"I UNDERSTAND THIS DOES NOT AUTHORIZE A GITHUB PUSH"}
```

Or choose `decision="Reject"`. The request cannot be replayed after a
decision and rejects expired/changed F-039, bundle or F-032 proofs. The
superadmin can acknowledge the human intent but **cannot grant a write
capability**. A policy result other than `Observed` blocks
acknowledgment; visible active rulesets remain advisory risks, never
implicitly waived for future execution.

Read-only endpoints:

```text
GET /api/incremental-preparations/{prepId}/operation-plans/{planId}/transfer-workpacks/{workpackId}/import-intents
GET /api/incremental-preparations/{prepId}/operation-plans/{planId}/import-intents/{intentId}
GET /api/incremental-preparations/{prepId}/operation-plans/{planId}/import-intents/{intentId}/events
```

These contain only digests, bounded counts, actor UUIDs, timestamps,
states and safe policy categories. No secret or raw Git history is
returned. Pending/Acknowledged/Rejected are **human review statuses**.
No status means `writeCapabilityVerified=true`.

## Durability and concurrency

`github_import_intents` is unique per F-039 readiness ID. Enqueue is
idempotent only for the *exact* same actor, policy/ref SHA, and request.
The source-scoped PostgreSQL advisory lock serializes creation and
single-shot decision changes across API replicas. The audit events
(`Requested`, `Acknowledged`, `Rejected`) append transactionally
through `INSERT ... RETURNING` / `UPDATE ... RETURNING`, protected
against double decisions. SQL CHECK enforces two distinct actor UUIDs,
ten-minute expiry, only known policy/ref statuses and the permanent
no-write barrier, including on tables created by EF `EnsureCreated`.

## Limitations

A completed two-person review **does not** permit GitHub import.
Before any executor, implement independently verified effective write
scope and branch/ruleset controls, short-lived destination capability,
explicitly approved non-destructive uploads, atomic per-ref
compare-and-create on GitHub, re-hashing the actual bundle at the
moment of transfer, and complete post-import SHA verification.

Policy lookups exercise the default branch and inherited rulesets
only; their absence cannot certify that all future created branches
are unprotected. An incomplete ruleset page is not permission.
Tenant-specific GitHub live writes are not exercised in CI.
