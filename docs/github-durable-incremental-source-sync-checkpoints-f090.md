# F-090 — durable, resumable, source-only incremental checkpoints

## Purpose and strict boundary

F-089 already shows source-staging evidence per repository from an
immutable reviewed F-029 plan. F-090 makes each subsequent
**PostgreSQL-staged snapshot** independently check-pointable, with
an unbroken predecessor chain. These are source-only checkpoints:
they do **not** authorize GitHub pushes, retry a sender, attest to
remote object closure or mark migration complete.

A checkpoint is not a running job; it is an immutable reconciliation
of historical F-025 source change counts. The existing F-008 Azure
DevOps discovery/staging job still performs source refresh. Operators
can explicitly record F-090 checkpoints after a completed staging
epoch, without copying refs or OIDs into the HTTP request.

## Data model

One `github_source_sync_checkpoints` record contains:

- F-029 plan, F-026 preparation, source connection and committed
  F-025 snapshot GUIDs.
- Original **manifest SHA-256**, source collection and PostgreSQL
  staging timestamps, source inventory completeness.
- Previous checkpoint UUID + **previous evidence SHA-256**, or null
  only for the exact original F-029 source snapshot.
- Four whole-source F-025 journal counters, the *total ref change*
  count and number of ref changes belonging to **unmapped**
  repositories (never dropped from the accounting).
- Sorted, bounded per-repository `Added/Changed/Restored/Missing`
  counters for the 1–10 **original F-029 mapped source repositories**.
- A stable SHA-256 over the complete canonical epoch payload,
  a distinct server-generated receipt ID, operator and PostgreSQL
  timestamp. `CanPush`, `CanRetry`, `CanDispatch` and
  `MigrationComplete` remain false.

Ref names, Git OIDs, Git object contents, access tokens and provider
payloads are not serialized into this table or REST DTO.

An immutable event table stores one independent `Recorded` audit
for every receipt. PostgreSQL CHECKs, unique plan+snapshot, a unique
successor per predecessor, a partial unique baseline, foreign keys,
and UPDATE/DELETE rejection triggers survive process restarts.
Schema bootstrap installs those constraints even when EF
`EnsureCreated` had already materialized mapped tables.

## Exactly-once progression and performance

1. Read the original F-029 plan and verify its immutable manifest DAG
   using the existing repository. The request must include its
   expected manifest SHA.
2. Start a PostgreSQL transaction, lock the **same source connection
   advisory lock as F-013 staging** (`hashtext(sourceId), 732911`)
   followed by the F-090 plan lock (`hashtext(planId), 732990`).
3. Revalidate the F-029 plan. If the exact requested snapshot was
   already checkpointed under the same predecessor+manifest,
   return its verified original receipt **without writing an event**.
4. Require the request's expected predecessor checkpoint UUID to
   equal the actual last checkpoint. The first eligible snapshot
   must equal the plan's original baseline; subsequent snapshots
   must be the **next** committed stage of the same source in strict
   collection-time order. Never skip a snapshot to simulate a
   catch-up or implicitly rebase an earlier plan.
5. Re-read the committed F-025 stage/journal integrity summary.
   Query the exact `source_id::jsonb ->> 0` repository identity,
   aggregated in PostgreSQL for at most 10 mapped source repos, plus
   a whole-source ref count. The original baseline must match
   F-029's sealed per-repo totals.
6. Persist the canonical hashed epoch and one append-only audit
   event atomically. Distinct replicas receiving the same request
   converge on the **same receipt ID and SHA**.
7. List history in bounded keyset pages (`take` 1–10), verify
   every returned row SHA, event, predecessor reference and current
   source journal counts, and expose only the one next eligible
   snapshot ID. A stale cursor/foreign plan cannot read another
   plan's history.

No out-of-order, duplicate event, leapfrogged epoch or automatic
GitHub replay is permitted.

## API + UI

- `GET /api/incremental-preparations/{id}/operation-plans/{planId}/source-sync-checkpoints?take=10&after={checkpointId}`
- `POST /api/incremental-preparations/{id}/operation-plans/{planId}/source-sync-checkpoints/{snapshotId}` with JSON `expectedManifestSha256` and optional `expectedPreviousCheckpointId`.

Both routes require `PasswordChanged`, `superadmin` and the
`staging-sync` rate limiter. F-090 is **disabled by default** via
`GitHubSourceSyncCheckpoints:Enabled=false`. No GitHub App
credentials, sender, target URL or Git commands are reachable
through this service.

F-089's Source sync page gains a checkpoint timeline with a
single **Record next checkpoint** action: it submits the exact
server-provided eligible snapshot plus expected predecessor+plan
SHA, after explicit user confirmation. It shows chained hashes,
timestamp, ref deltas by repo, unmapped counts and partial-source
warnings. The UI refuses a response claiming push/retry/dispatch/
migration completion. If a second operator records first, the
stale cursor fails closed and the operator must reload.

## Validation and remaining steps

- Unit tests validate stable SHA-256 across JSON roundtrip,
  predecessor hashes, exact sums, sorted source mappings, and
  immutable non-executable semantics.
- PostgreSQL 17 tests exercise original F-029 baseline, new source
  snapshots in chronological order, prevention of out-of-order
  jumps, reboot-safe history, cursor pagination, six API replicas
  racing on a single epoch, read-only idempotent replay,
  wrong manifest/cursor and SQL UPDATE/DELETE guards.
- API/TestServer tests enforce superadmin-only access, disabled
  defaults and no external provider calls. Frontend/Playwright tests
  verify the manual progression, safety flags and refusal on drift.
- This captures **counts and provenance**, not a cryptographic
  digest of every newly observed Git ref payload. A future F-091
  may extend the source evidence with a scalable hash manifest of
  all changed ref OIDs. For destination synchronization, F-083
  must first be authorized and validated on an actual sandbox;
  F-090 alone does not send anything.
