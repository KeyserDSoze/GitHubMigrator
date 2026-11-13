# F-073 — terminal GitHub ref and repository-identity reread before preflight sealing

## Problem

F-072 verifies the GitHub App installation's exact organization and app
identity before requesting a one-repository ephemeral token. F-043 binds
that token to a numeric repository ID and requires a visible, non-archived,
non-disabled repository. F-051 uses that token to GET complete heads and
tags (including annotated **tag object** OIDs), then GET ruleset and
branch-policy observations.

Those network requests are **not a single GitHub transaction**. A branch,
annotated tag or repository's numeric identity could change after the first
ref observation but before the policy inspection finishes. A result sealed
against the earlier refs would present obsolete evidence to operators.

## F-073 implementation

Inside the existing `GitHubAppScopedWriteCredentialInspector`
implementation of `IGitHubRemoteAppPreflightReader`:

1. Keep the F-072 App-JWT installation GET, narrow token mint, token-bound
   initial exact-repository metadata GET, initial heads/tags GET, and
   policy/ruleset GET sequence unchanged.
2. **After every policy GET has returned**, use the **same restricted
   ephemeral token** to issue a *second* complete
   `GitHubRefObservationReader.ReadAsync` for heads and tags.
3. Compare all canonical typed ref entries (name, Git OID, **object type**)
   in stable order with the initial observation. Any missing, added,
   moved or changed ref raises `RemotePreflightRefsChangedDuringReview`
   and **no F-051 receipt** is returned.
4. Finally, issue a fresh **GET** of the exact
   `https://api.github.com/repos/{organization}/{repository}` resource,
   verify the same numeric repository ID, full name, owner and
   `archived=false` / `disabled=false`. Unknown status, 404,
   body/JSON/duplicate-field failure, repository rename or ID replacement
   maps to `RemotePreflightRepositoryChangedDuringReview`.
5. Return only the **final** typed ref observation to the F-051
   cryptographic attestation. The existing F-051 service still
   revalidates PostgreSQL packet/fence/attempt after remote reads and
   seals a **read-only, write-disabled** evidence receipt.

The existing initial metadata GET has been extracted into a private
`VerifyRepositoryWithTokenAsync` helper so that initial and final checks
share **identical** bounded-JSON and strict numeric identity validation.
No new HTTP client, authentication path, repository creation, user URL,
Git operation or GitHub mutation is introduced.

All calls use fixed `api.github.com` URLs and the original configured
non-redirecting HTTP client. The one-repository token remains internal
to the adapter, and no payload, token, private key or raw server error
is logged or returned.

### Limits and guarantees

- Double-read proof is an **optimistic consistency check**, not a
  GitHub-side lock or an atomic snapshot. Changes that happen and
  revert between the reads, or changes *after* the final read, may
  remain undetected. Actual execution will need another
  immediately-before-write baseline fence and destination-side
  compare-and-create semantics.
- GitHub App installation ownership, reported Contents permission,
  ref/ruleset observations and valid review receipts **never**
  constitute permission to push. No code path authorizes remote
  writes, sets `MigrationComplete`, marks an execution ready or
  automatically retries an ambiguous transfer.
- The final read incurs two extra heads/tags GETs and one metadata GET
  on the F-051 preflight path. Existing bounded token deadlines and
  F-051 18-second overall timeout still apply; timeouts fail closed.
- No PostgreSQL schema/index changes are required. F-073 stacks on
  F-072 PR #73; `main` remains unchanged.

## Automated contract verification

GitHub API HTTP-handler tests exercise the full wire-level sequence and
assert exactly one token POST:

- Clean path: installation GET → token POST → initial metadata GET →
  heads/tags GET → metadata and policy GETs → **heads/tags GET again →
  final metadata GET**. No GitHub content write.
- A branch OID or **annotated tag object** OID changed between ref
  reads is rejected before final identity GET.
- A final repository ID swap, wrong owning organization, archive,
  disable, duplicate JSON identity field or HTTP 404 is rejected even
  when both ref readbacks were identical.
- Errors are sanitized and contain no ephemeral token.

Regression suite: .NET 10/xUnit, real PostgreSQL 17/Git integration,
React/Playwright and Terraform validate.
