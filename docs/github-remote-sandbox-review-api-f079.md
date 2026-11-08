# F-079 — production-wired, write-disabled GitHub sandbox review API

## Purpose

F-075/F-076/F-077/F-078 provide a trusted, end-to-end **read-only**
assessment of a proposed create-only GitHub migration and a permanently
non-executable PostgreSQL session receipt. Until F-079 that flow was
internal only: production DI had no registration and there was no
authenticated HTTP entrypoint.

F-079 connects the existing trusted services to the API while retaining
their explicit **no remote writes** boundary. It also corrects a cold
start issue: the F-078 session table references the F-055 hazard table,
so F-055 must be initialized **before** F-078 on a new PostgreSQL database.

## Routes (superadmin only)

Both routes live under
`/api/incremental-preparations/{preparationId}/operation-plans/{planId}/github-app-scopes/{scopeId}/remote-sandbox-review-sessions`.

- `POST /` with `{ "hazardId": "UUID" }` requests an authenticated,
  GET-only review. The server resolves the exact historical F-051 receipt
  through F-055/F-052/F-077, regenerates source proof from persisted
  F-044 data and the physical bundle **before and after** GitHub App reads,
  checks current target refs and policy, reruns the F-075 PostgreSQL
  authority verifier under repository locks, and appends an F-078 session.
  Caller-supplied GitHub URLs, refs, refspecs, source digests, provider
  tokens, arbitrary preflight receipts and execution commands are not
  accepted.
- `GET /{sessionId}` returns the F-078 immutable receipt and its
  independently checked `ReadOnlyReviewCurrent` or `HistoricalOnly`
  inspection. Before the session is even loaded, the API verifies that
  its persisted F-052 grant belongs to **the exact preparation, plan,
  scope and allowlisted numeric repository** from the route.

Both require JWT `PasswordChanged` and role `superadmin`. Provider
reviews and receipt reads share an isolated eight-requests-per-minute
fixed-window limiter. Disabled configuration rejects both with
`409 SandboxReviewSessionDisabled` before any target-network activity;
other invalid/stale authority evidence fails closed with a stable error
code. Unknown or cross-scoped receipts yield 404.

## Production dependencies and safeguards

DI now registers the concrete F-044 source packet reader, F-075 PostgreSQL
authority verifier, F-078 PostgreSQL session journal and F-076 trusted
GET-only orchestrator. F-077 remains the existing production immutable
preflight journal. The reader still requires the explicit
`GitHubRemoteSandboxFence`, `GitHubAppWriteCredential` and
`GitHubRemoteTransmitHazard` allowlist/feature switches before external
reads, with the existing credential source and one-repository GitHub App
identity restrictions.

A successful POST returns hashes, IDs and permanently false write flags,
**not** a privileged token. It never invokes Git push, a GitHub REST
mutation, branch/tag creation, an execution queue, automatic retry after
an ambiguous send, or a migration-complete transition. GET is not an
authorization; its status may become historical immediately after the
response.

This API supports a **manually prepared, test-only sandbox**. It does
not claim general-purpose remote migration is implemented. The eventual
remote sender must pass a separate reviewed write-capability protocol and
remote object/ref readback. No automatic recovery of F-055
`MayHaveTransmitted` is permitted.

## Verification

- JWT integration coverage for anonymous, user, admin and superadmin
  requests; both verbs fail closed with all switches disabled and make
  no provider calls.
- Existing F-076 domain tests: trusted source regeneration, GET-only
  remote observation, stale evidence/identity/policy and no-write rules.
- Existing F-075, F-077 and F-078 PostgreSQL 17 integration tests:
  independent authority, immutable preflight/session receipts,
  concurrency, tamper resistance, and no automatic retry after hazard
  marking.
- The API test host initializes the real schema at startup, exercising
  the corrected dependency order. CI covers .NET/xUnit, PostgreSQL 17,
  React/Playwright and Terraform.

Review F-079 as a stacked PR on F-078. `main` is not modified.
