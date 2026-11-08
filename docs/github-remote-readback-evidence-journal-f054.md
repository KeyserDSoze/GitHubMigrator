# F-054 — Immutable PostgreSQL remote readback evidence journal

F-053 introduced independent, bounded GitHub commit and annotated-tag GET
readback. F-054 persists **the resulting reviewed observation** in
PostgreSQL, without introducing a sender, unverified migration-success
claim, or automatic retry.

## Durable record and controls

- One immutable readback evidence record per F-052 reviewed, **write-disabled**
  grant; uniquely linked by grant ID and numeric GitHub repository ID.
- Domain recalculates F-053 object-tip assessment and F-052 complete-ref
  classification, and compares the *exact* original F-051 baseline
  fingerprint, including annotated-tag object types, and F-044 create-only
  candidate SHA against the PostgreSQL F-052 grant.
- Before inserting, the journal takes the **same numeric-repository PostgreSQL
  advisory transaction lock as F-050 and F-052**, then re-reads the live
  persisted F-052 grant, F-050 reviewed fence and F-047 blocked attempt.
  Every independent observer must differ from all prior reviewers/proposers.
  Any expired, modified, unreviewed or write-authorizing dependency fails
  closed, including if a separate replica changes it during a provider read.
- The single INSERT and its audit event commit atomically. Concurrent
  replicas recording an identical receipt return the original record;
  divergent receipts or a new observer for the same grant are rejected.
- Database SQL CHECKs prohibit transport_attempted, remote_writes_authorized
  and may_automatically_retry in **every possible state**. Rows and event
  records reject updates and deletes through database triggers.
- The journal stores only hashes, identifiers, timestamps, state, and
  actor ID. No Git ref names, PATs, installation tokens, Git URL or private
  signing keys are persisted in these tables.

## States and meaning

- ExactTipEvidenceOnly: a provider-sealed readback reported matching
  complete refs **and** selected tip commit/tag objects; not proof of a
  successful send, object-history closure or migration completion.
- OperatorReviewRequired: full remote refs differ despite the inspected
  tips, requiring a human to reconcile.
- No transition to sending, retrying or verified-full-history exists.

The schema is initialized after F-052 at API startup. A scoped internal
journal service is registered, **without** an HTTP endpoint or background
sender. This stage intentionally cannot automatically initiate readback:
F-051's short-lived installation token is not re-exposed or persisted,
and no existing code path is authorized to perform remote Git mutations.

## Explicit remaining work

A future actual GitHub test-repository sender requires an independent
approval with an explicit test repository allowlist, an atomic
pre-transmission MAY-HAVE-SENT state committed BEFORE any outbound Git
transport, crash recovery that cannot blindly resend, a fresh scoped
GitHub App token and independent remote reconciliation. F-054 does
**not** implement or pretend to enter a MAY-HAVE-SENT state, since no
transport exists yet. F-053 only verifies selected object tips, **not**
complete history/trees/blobs/LFS/signatures or source-target object closure.

## Tests

Real PostgreSQL 17 integration exercises six concurrent replicas,
idempotent exact replay, refusal of mismatched proofs, stale F-050
fences, fifth-actor independence, immutable events and direct SQL
denial of authority flags. Domain xUnit verifies deterministic SHA
and original annotated-tag baseline typing, ambiguous full refs,
forged tip SHA and stale observations. React/Playwright and Terraform
gates remain part of the normal PR CI.
