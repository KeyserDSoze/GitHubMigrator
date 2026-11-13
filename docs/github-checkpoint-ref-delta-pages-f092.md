# F-092 — Bounded verified source-ref change pages

F-092 makes the F-091 checkpoint proof operationally inspectable.
An authenticated, password-changed **superadmin** can read a limited
keyset page (1–100 items) of canonical source ref changes associated
with one already committed version-2 checkpoint.

Every response is bound to the source snapshot, checkpoint ID, original
F-091 ref manifest SHA-256 and the bound F-091 checkpoint attestation.
Before paging, PostgreSQL verifies the immutable checkpoint audit,
F-090 count proof and F-091 full source-ref digest. The page is fetched
from the original F-025 staging journal, never from the mutable current
staging catalog. Cursors are exact canonical F-013 [repositoryId, refName]
JSON keys, checked against the selected snapshot. A mapped repository
uses the already-reviewed F-029 target name; unknown source repos
remain explicitly unmapped.

This is a **read-only evidence and diagnosis feature**, not migration
dispatch or safe permission to push. It exposes SHA-256 fingerprints
of staged ref payloads, not GitHub secrets or raw provider credentials.
It does not claim provider freshness, object closure or destination sync.

The React Source sync workbench lets a superadmin expand a version-2
checkpoint and inspect paginated, read-only ref changes with the reviewed
target mapping, before/after fingerprints and explicit unmapped labels.
The browser rejects unexpected authority, malformed pages, cross-checkpoint
attestations and non-monotonic cursors; no write endpoint is called.

A version-1 historic F-090 checkpoint is intentionally refused for
per-ref authenticated viewing: its count-only SHA did not commit to
F-091's individual ref evidence.

A full manifest recheck is performed for each page until a durable
verified cursor/cache primitive is added. Actual page rows are bounded
by the keyset limit; hash verification remains proportional to total
ref changes in the checkpoint. This trade-off preserves correctness
without claiming O(page-size) end-to-end complexity.
