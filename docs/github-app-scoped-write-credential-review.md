# F-043 — GitHub App write-scope evidence (NO REPOSITORY WRITES)

## Scope

After F-042's SHA-sealed, fresh (30-second) `RefSetUnchanged`
observation, F-043 independently inspects **one new GitHub App installation
credential**. A one-repository installation access token with
`Contents:write` is requested from GitHub, inspected with a GET, and
**immediately discarded**. No token crosses the API/business persistence
boundary. **No repository ref is created, deleted, updated or pushed.**

This is materially stronger than F-039's coarse
`permissions.push=true`: the App token issuance response includes the
exact requested `permissions`, `repository_selection`, a single
repository identity and an expiration. It still does **not** prove the
Git transport will work, authorize a push, bypass branch rulesets or
prove a non-destructive lease. `writeCapabilityVerified`,
`executionAuthorized`, `remoteWritesPerformed` and
`readyForExecution` remain **false** in PostgreSQL.

## Configuration: OFF by default

```text
GitHubAppWriteCredential:Enabled=true
GitHubAppWriteCredential:AppId=<GitHub App numeric ID>
GitHubAppWriteCredential:InstallationId=<GitHub App installation numeric ID>
GitHubAppWriteCredential:PrivateKeySecretReference=<dedicated Key Vault secret name>
GitHubAppWriteCredential:AllowedRepository=org-name/test-repository
```

Provision a **dedicated test-only GitHub App**, installed on exactly one
disposable repository. Configure repository permissions **Contents:
read/write** and built-in Metadata:read; avoid Administration, Workflows
and organization privileges. The PEM private key MUST remain in Key Vault
(or the local encrypted development secret store), never in appsettings,
source control, API requests, logs or Git processes. Do not share its
secret reference with the existing read-only migration connection.
For production, use an operator-managed secret rotation and separately
hardened service principal / managed identity.

Without `Enabled=true`, nonempty exact allowlist, App ID, installation
ID and valid secret reference, the POST fails before any credential or
GitHub access. The F-043 endpoint is further limited to a *superadmin
who is the same independently confirmed reviewer from F-040*, not the
original requester.

## API

```http
POST /api/incremental-preparations/{prepId}/operation-plans/{planId}/remote-fences/{fenceId}/github-app-scope
Content-Type: application/json
Authorization: Bearer <superadmin-jwt>

{
  "expectedFenceEvidenceSha256": "<F-042 SHA>",
  "acknowledgementText": "I UNDERSTAND THIS TOKEN IS DISCARDED AND DOES NOT AUTHORIZE A PUSH"
}
```

```text
GET /api/incremental-preparations/{prepId}/operation-plans/{planId}/github-app-scopes/{scopeId}
GET /api/incremental-preparations/{prepId}/operation-plans/{planId}/remote-fences/{fenceId}/github-app-scopes
```

The GETs reveal only repository identity, installation ID, timestamps,
SHA-256 fingerprints, attestation status and permanently false execution
flags. They **never** reveal JWT, installation token, signing key,
connection PAT, private key reference or Git push command.
A separate endpoint limiter bounds App token requests.

## Credential protocol

1. Read a *current, matching* F-042 fence and F-040 two-person
   approval. Check `AllowedRepository` against the F-039 immutable
   mapping; reject any actor other than the second reviewer.
2. Resolve the distinct GitHub App PEM private key by its configured
   encrypted secret reference. Build an 8-minute `RS256` GitHub App
   JWT with a 60-second clock-skew allowance.
3. `POST https://api.github.com/app/installations/{id}/access_tokens`
   with exact JSON
   `{"repositories":["test-repository"],"permissions":{"contents":"write"}}`.
   This POST **issues a credential at GitHub**, but does **not** mutate
   the repository. The HTTP client disables redirects and cookies.
4. Require HTTP 201, bounded <=32KiB JSON, `repository_selection=
   selected`, **exactly one** returned repository with correct numeric
   ID/owner/full_name, `permissions.contents=write`, no unexpected
   permissions, and expiration 3–65 minutes into the future.
5. Use only the minted token to perform
   `GET https://api.github.com/repos/{org}/{repo}`; verify its numeric
   repository ID, exact owner/full name, unarchived and enabled state.
   No Git command or repository-changing REST call is issued.
6. **Discard** the short-lived installation token. The adapter returns
   a secret-free categorical observation to the business layer.
7. Under the original source-scoped PostgreSQL advisory lock, recheck
   F-029/F-039/F-040/F-041/F-042 lineage and **rehash the entire physical
   bundle again**. Record an immutable, SHA-256-sealed issuance
   attestation unique by F-042 fence. Two-instance replay retains
   the first evidence and rejects changed actor or repository.

Even when `scopedContentsWriteReported=true` and the row's
`isCurrent=true`, there is **no reusable token**, no remote operation,
no queued transfer, and no execution permission. The F-042 snapshot can
still expire or GitHub refs can change immediately after it.

## Tests and limits

Unit tests generate real RSA keys and verify the JWT signature, exact
one-repository token mint JSON, follow-up GET authentication, no
network access on invalid key, rejection of broad permissions or
repository scope, 403, archived destination and response confinement.

ASP.NET + PostgreSQL 17 integration uses a **test-only synthetic
issuance observation** (never a live minted token) for atomic
idempotency, source-lock proof binding, reviewer separation,
superadmin-only JWT and SQL CHECK denials of all four write/execute
flags. Real F-038 disk checks and F-041 Git import are separately
covered by actual Git integration. No GitHub live-write credential is
used in CI, and GitHub App installation tests use a mocked HTTP
transport.

Still required for a real GitHub migration: a per-run capability
authorization process, a worker that obtains and holds a new
one-repository token *only for that run*, branch/ruleset permission
assessment on **each** ref, remote atomic compare-and-create leases,
retry/recovery, and independent post-write SHA verification.
A F-043 issuance record must never itself be treated as that
authorization.

References:
- https://docs.github.com/en/rest/apps/apps#create-an-installation-access-token-for-an-app
- https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/authenticating-as-a-github-app-installation
