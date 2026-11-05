# F-027 — Independent incremental planning review and database-only dry-run

F-027 continues F-026's durable, destination-bound **review-only** preparation
workflow. It introduces an explicitly recorded, two-person **planning review**
before any later work can consider destination execution. Importantly, an
`ApprovedForPlanning` decision is **not authorization to write** to GitHub
or to grant effective permissions.

## Safety boundaries and provenance

A preparation can be reviewed only when F-026 has finished in `Prepared`,
with a stable, SHA-256-sealed evidence digest and an existing F-025 version-1
journal. The source and destination are pinned to the preparation's UUIDs;
both connection metadata records must remain independently `Verified`.
This check is **database only**: F-027 does **not** perform live GitHub API
preflight or contact any provider. The next phase must introduce a fresh
read-only target preflight and separate execution gates.

Two new PostgreSQL tables store no credentials, source payloads or target
commands:

- `incremental_preparation_reviews`: one review per preparation, state
  `DryRunReady`, `ApprovedForPlanning`, `Rejected` or `Revoked`;
  source/destination validation timestamps, F-026 evidence SHA,
  cryptographically bound dry-run SHA, item/security/removal counts,
  reviewer UUID, decision timestamp, optional allowlisted reason code and
  sensitive-change acknowledgement.
- `incremental_preparation_review_events`: append-only audit of
  `DryRunReady`, `ApprovedForPlanning`, `Rejected` and `Revoked`,
  actor UUID, timestamp, dry-run SHA and optional reason code. Idempotent
  retries never duplicate a successful event.

The bootstrap creates both tables and indexes additively. This remains a
phase-0 development upgrade, not a production-reviewed migration rollout.

## Fail-closed dry-run

`POST /api/incremental-preparations/{id}/dry-run` runs under a PostgreSQL
transaction with the **same source advisory lock as F-013 staging and F-026
preparation**. It checks the latest staged source snapshot, both validated
connection records, and the immutable F-025 journal's aggregate counts.

It additionally compares the **full F-025 journal and F-026 prepared rows in
both directions with `EXCEPT ALL`**, including asset identity, change kind,
old/new SHA, revision, review category and `NeedsReview` state. Merely
matching row counts is not sufficient. The F-026 SHA-256 is independently
recomputed by streaming the ordered prepared items and checked against the
persisted digest without materializing the entire review set in RAM.

The dry-run's SHA-256 binds:

- preparation, snapshot, source and destination UUIDs;
- F-026 evidence SHA, total and sensitive item counts, removal count;
- source/destination validation timestamps.

Revalidation of either connection changes the dry-run proof, requiring a
new verification before approval. A repeated, identical dry-run is
idempotent. Source evidence or tampering errors roll back the review
transaction and leave no false `DryRunReady` state.

## Independent human review

The authenticated admin or superadmin may run a dry-run and reject a
preparation. **Only a superadmin** can grant `ApprovedForPlanning` or
revoke it. The application and database independently ensure that the
reviewer UUID is **different from the F-026 enqueueing actor**.

Approval requires the exact expected dry-run SHA and an explicit
acknowledgement when security changes or removals are present. Decisions
are transactionally fenced, mutually exclusive, and irreversible except
for an audited revocation of a planning approval. A rejection remains
terminal; a revoked approval cannot be re-approved. A reason is a bounded
alphanumeric code (required for rejection/revocation), not a free-text
exception or potential credential leak.

An older snapshot can retain its historical approval, but `isCurrent`
becomes false as soon as a newer staged snapshot is available or a
connection's validation status/timestamp changes. **No executor consumes
this planning approval**, even if `isCurrent` is true.

## Protected APIs and workbench

Every endpoint inherits the existing `PasswordChanged` and
admin/superadmin JWT policies:

| Endpoint | Function |
| --- | --- |
| `POST /api/incremental-preparations/{id}/dry-run` | Verify read-only provenance and store a sealed, versioned dry-run proof. |
| `GET /api/incremental-preparations/{id}/review` | Review state, actor, risk counts, SHA and `isCurrent`. |
| `POST /api/incremental-preparations/{id}/review/decision` | `ApproveForPlanning` (superadmin) or `Reject`, with expected dry-run SHA, optional risk acknowledgement and reason code. |
| `POST /api/incremental-preparations/{id}/review/revoke` | Superadmin-only audited revocation. |
| `GET /api/incremental-preparations/{id}/review/events` | Latest 100 review events, no provider payload. |

Invalid inputs produce HTTP 400, missing preparation/review 404, conflicts,
outdated evidence and missing acknowledgements 409, and insufficient
privilege 403. POST endpoints are rate limited. The IT/EN Discovery UI
exposes the dry-run, digest, review state and evidence freshness and only
shows the planning-approval control to superadmins. No execute button or
GitHub writer is introduced.

## Regression tests

Real PostgreSQL 17 integration verifies >500 changes and security ACEs,
deterministic dry-run replay, independent-review and sensitive-change
requirements, equal-count tampering in either journal or preparation
items, concurrent contradictory decisions (exactly one wins), rejection
and revocation idempotency, connection revalidation invalidation,
superseded snapshots and historical `isCurrent=false`.

Real ASP.NET/JWT tests exercise anonymous/user denial, admin dry-run,
superadmin-only approval and revocation, invalid SHA/conflicts, audit
events, and provider stub call counts: **zero new Azure DevOps/GitHub
requests** throughout the review.

All existing F-001–F-026 tests remain in the CI matrix (.NET10/xUnit,
PostgreSQL17, React/Playwright, Terraform).

## Explicitly not implemented

No outbound GitHub repository/ref creation, Git history transfer, delete
or ACL grant; no execution authorization, target-side dry-run comparison,
real-time GitHub permission revalidation, production schema migration,
scheduled sync polling, enterprise throughput benchmark or secure
retention/compaction. These require separate audited implementation steps.
