# F-030 — Provenance-bound, read-only Git branch/tag reconciliation

## Scope and status

F-030 adds a **pure business-layer reconciliation component**, not a live
GitHub operation or end-to-end API. It compares independently supplied,
already verified source and destination ref observations. It does **not**
load the F-014 PostgreSQL chunks, call GitHub, fetch Git objects, establish
fast-forward ancestry, enqueue a migration job or push any Git ref.
The F-029 manifest remains blocked and F-029 authorization remains denied.

This is the first reusable branch/tag diff core for F-006. The new types are:

- `GitMirrorRef` — fully qualified Git ref and 40-/64-hex object ID;
- `GitMirrorRefReconciliationRequest` — F-029 plan ID, its immutable manifest
  SHA-256, independently prepared source and target observation digests and
  their bounded ref sets;
- `GitMirrorRefReconciler.Compare` — deterministic, validated comparison;
- `GitMirrorRefReconciliation` — counts, sorted per-ref decisions, SHA-256
  evidence hash and always-false `ReadyForExecution`.

The caller must validate the provenance and freshness of the three supplied
proofs itself. Supplying arbitrary SHA strings is **not authentication** or
proof that network observations exist. No external request is made by the
business comparison component.

## Conservative per-ref decisions

| Decision | Meaning | Safety outcome |
| --- | --- | --- |
| `MatchingObjectId` | Exact ref name and same normalized Git OID | No target action; not an execution permit |
| `CreateCandidate` | Source ref observed; no corresponding target ref provided | Blocked: `DestinationWritePermissionUnverified` |
| `DivergedBlocked` | Both observed with different OIDs | Blocked: `RefFastForwardUnverified`; no force-push inference |
| `TargetOnlyPreserved` | Target ref observed but absent from source | Preserved; `TargetRefDeletionProhibited` |

A missing source ref is **not evidence of a deletion**: source page coverage,
stale observations and intentionally scoped selections must be accounted
for before any deletion workflow is considered. A 40- or 64-hex OID only
identifies a Git object; matching OIDs do not prove identity permissions,
complete object reachability, target write scopes or an actual completed
migration. Annotated tag OIDs are compared as Git object IDs, not as
peeled commit IDs; differing tags are blocked.

## Fail-closed input invariants

1. All three proof digests are exactly 64 lowercase hexadecimal characters
   and the operation plan ID is nonempty.
2. At most **25,000 refs per side**. Larger data requires a paged,
   checkpointed adapter rather than unbounded in-memory comparisons.
3. Only complete `refs/heads/...` and `refs/tags/...` ref names are accepted.
   No pseudo-ref `HEAD`, `refs/pull/...`, remote-tracking refs or notes.
   Reject Git-invalid control/whitespace and special sequences, component
   aliases, `.lock` suffixes and names exceeding 255 UTF-8 bytes.
4. Each side has no duplicate or case-folded alias names. Cross-side names
   that differ only by case are rejected to avoid target-host ambiguity.
5. Every object ID is 40 or 64 valid hexadecimal characters. Mixed SHA-1
   and SHA-256 formats are rejected, even when ref names do not overlap.
   OIDs are normalized to lowercase before classification and hashing.
6. Reconciliation is sorted by ordinal ref name and SHA-256 hashes a
   versioned header binding all three proofs, counts, plan ID and every
   per-ref result with newline-delimited JSON. Reordering observations
   cannot change the evidence hash.

Memory is O(source refs + target refs); matching uses indexed lookups and
ordered enumeration, not a quadratic ref-to-ref scan. Current testing
includes 3,000 refs, deterministic reordering, proof tampering, mixed object
formats, invalid Git ref names, namespace case collisions, target-only
preservation and all-matching/empty non-executable results. A performance
benchmark with provider traffic and PostgreSQL streaming is still pending.

## Next steps before any real GitHub write

- Connect the immutable F-014 source ref chunk reader and new bounded,
  GET-only GitHub target ref collector to an auditable reconciliation
  snapshot; validate job/source/plan provenance, complete pagination and
  freshness, with per-repo object-format compatibility.
- Establish independent **write-scope and repository-creation authority**
  using an explicit safe mechanism; GitHub 404 and read-only organization
  membership are not permission grants.
- Introduce a separately reviewed execution authorization protocol.
  No existing `Denied` F-029 record may be reinterpreted as an `Approved`
  permit.
- Implement leased, idempotent Git mirror transport with transport-secure
  credential handling and checkpoint fencing. Never force-update or delete
  a target ref without a separately approved, verified policy.
- Verify all transferred branch/tag OIDs from live target observations
  and persist a definitive audit result; approval alone cannot indicate
  migration success.

F-030 does not change the PostgreSQL schema, public HTTP routes, React UI
or production deployment. CI runs the .NET10 xUnit business tests plus
the existing PostgreSQL17, React/Playwright and Terraform checks.
