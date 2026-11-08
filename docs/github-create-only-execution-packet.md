# F-044 — sealed create-only Git execution packet (READ-ONLY)

F-044 derives a **deterministic, SHA-256-sealed execution-candidate packet**
from *server-owned evidence* after F-043. It is deliberately **not an
executable work order, remote ref reservation, GitHub permission, or Git push**.

## Contract

A superadmin can inspect:

\`GET /api/incremental-preparations/{preparationId}/operation-plans/{planId}/github-app-scopes/{scopeId}/create-only-packet\`

The request contains only server-issued UUIDs. There is no caller-supplied
repository URL, refspec, Git command, token, PEM, SHA override or local path.
The response includes source/target identity, referenced evidence SHA-256,
an exact candidate-set SHA-256, counts of create/match/preserved refs and the
full immutable packet evidence SHA-256. It does **not** return ref names,
object bytes, Git command arguments, secrets or push-ready credentials.

\`state\` is always \`ReviewOnlyBlocked\`; the four flags
\`writeCapabilityVerified\`, \`executionAuthorized\`,
\`remoteWritesPerformed\` and \`readyForExecution\` are always false.

## End-to-end validation

1. Require current F-043 *reported scope* from a separate GitHub App,
   current F-042 matching remote-ref observation, unexpired F-040
   two-person acknowledgement, real F-041 rehearsal, F-039 repository
   mapping and the original F-029 plan.
2. Rebind the exact F-043/F-042/F-041/F-040/F-039 evidence SHA values, repo
   numeric ID and name, chosen destination connection, workpack, plan,
   bundle and reviewer. No actor may replace or reinterpret those artifacts.
3. Re-hash the **physical source Git bundle**. Page all F-032 comparisons
   from PostgreSQL using fixed 100-row pages. Abort on missing pages,
   stale reconciliation, unexpected cursor progression or more than
   50,000 comparison rows.
4. Validate canonical Git **heads/tags only**, sorted, nonduplicate refs,
   SHA-1/SHA-256 consistency and exactly the create-only dispositions.
   Any \`DivergedBlocked\` fails the packet. Target-only refs are preserved,
   never interpreted as deletion candidates.
5. Require 1–128 create candidates and exact F-040 safe-plan hash, F-041
   successful local create count and the *exact* source and created-ref
   hashes observed through real Git in the disposable rehearsal repository.
6. Re-hash the source bundle again after paging. Produce a stable,
   versioned SHA-256 over the full proof chain and exact candidate hash.

The packet is calculated on each GET and is **not persisted** or
an authorization. Even a matching F-042 GET-only observation can become stale
immediately; F-043's minted token was already discarded. This read-only
preview does not hold a multi-replica lock over remote state and cannot
reserve or prove a missing GitHub ref.

## Before a remote push can be implemented

A separate feature must obtain an independent execution authorization and
a new short-lived, single-repository write token **inside** an isolated worker;
verify branch/ruleset protection and effective identity for each candidate;
hold durable target-wide work coordination; recheck GitHub refs immediately
before the transfer; use remote compare-and-create semantics; and verify the
exact destination ref set and object IDs *after* the write. On an unknown,
timed-out or partially successful push, the worker must reconcile, never
blindly retry and never force-update an existing ref.

CI covers packet seals, stale evidence, fraudulent reviewer/repository,
source and rehearsal digest drift, malformed candidate sets, ref deletion
prohibition, candidate cap and permanently false execution flags.
