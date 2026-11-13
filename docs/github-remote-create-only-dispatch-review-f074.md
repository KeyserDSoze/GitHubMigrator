# F-074 — cryptographically bound, write-disabled create-only pre-dispatch review

## Motivation

F-044 produces a deterministic, **non-executable** create-only candidate
packet from persisted migration/workpack provenance. F-051 (hardened by
F-072/F-073) uses a one-repository GitHub App token to inspect remote
refs and policy. F-052 requires a separate operator to review a scoped
**write-disabled** test intent. F-055 records an irreversible
`MayHaveTransmitted` / `OperatorReviewRequired` outcome for ambiguous
sends: **no automatic retry**.

These four proof families existed separately. A future sender must never
treat a clean, recently observed GitHub ref set as permission to create
refs when the reviewed grant belongs to a different packet, when the
pessimistic hazard belongs to a different lease generation, or when an
earlier attempt may already have transmitted.

F-074 adds a **pure, cryptographically sealed last-mile review
attestation**, not a transport sender or a GitHub permission grant.

## New domain contract

`GitHubRemoteCreateOnlyDispatchIntegrity.Seal` consumes the server-held:

- F-044 `GitHubCreateOnlyCandidatePacket`, including **canonical**
  heads/tags candidates and its immutable candidate-set digest;
- F-051 `GitHubRemoteAppPreflightReceipt` with the verified packet SHA,
  candidate-set SHA, original+fresh policy evidence, full typed target
  baseline fingerprint, repository ID, fence request/generation and
  fresh token-expiry evidence;
- F-052 `GitHubRemoteTestGrantStatus` in
  `ReviewedWriteDisabled` with an independent human reviewer and
  matching packet, policy, baseline, preflight, repository, preparation,
  plan, scope, fence and generation;
- F-055 `GitHubRemoteTransmitHazardStatus` in **exactly**
  `PreparedWriteDisabled`, with matching grant/packet/baseline/epoch
  and no marked actor, invoked transport, remote write or retry flag;
- a **newly observed and source-sealed** F-031 GitHub full typed ref
  snapshot, owned by the same organization/repository, and an
  independent review actor.

It fails closed if the candidate list is noncanonical, duplicates
existing target refs, leaves the `refs/heads/` or `refs/tags/`
namespaces, mismatches its F-044 cryptographic digest, diverges from the
F-051 typed baseline, changes an annotated **tag object's** OID,
exceeds the small 16-candidate F-046 budget, or has stale/contradictory
timestamps. It separately refuses any mismatched original policy or
current policy lineage, wrong numeric repo, stale grant, conflicting
review actor, revoked/superseded hazard, old generation, past deadline
or possible transmission.

The returned `GitHubRemoteCreateOnlyDispatchReview` contains **only**
immutable IDs, SHA-256 evidence digests, counts, a short expiration and
the explicit state `ReviewedWriteDisabled`. Four safety flags are
**always false**:

- `RemoteTransportInvoked`
- `RemoteWritesAuthorized`
- `MayAutomaticallyRetry`
- `MigrationComplete`

No raw ref names or OIDs, bearer token, signing key, refspec, repository
URL, subprocess command, external HTTP write, API action or PostgreSQL
write is emitted by this component.

The attestation's SHA-256 digest includes a domain/version separator,
all critical F-044/F-051/F-052/F-055 IDs and digests, the fresh target
observation's SHA-256, the independent actor, expiration and all
write-disabled flags. It expires at the **earliest** applicable
grant/hazard deadline, short observation window or token-expiry floor.

## Operational security boundary

**No API or production DI integration is added.** This is a reusable
server-side domain validator, not a GitHub migration capability.
A domain caller can pass constructed DTOs; that **cannot establish
authenticity** by itself. A future orchestrator must fetch all four
records from their authoritative PostgreSQL stores, revalidate their
mutable lock/generation under the appropriate transaction, and obtain
the final typed GitHub observation using the one-repo token. The receipt
must never be accepted as an execution capability by itself.

The fresh GET remains an optimistic observation, **not** an atomic
GitHub snapshot or compare-and-create. To ever enable remote writes,
a separate reviewed implementation needs a narrow destination-only
GitHub sender, atomic/non-destructive ref creation, hard PostgreSQL
outbox/lease fencing, cancellation, no-auto-retry after possible
transmission, and independent post-send object closure/readback.
Existing production write barriers remain entirely closed.

## Test coverage

The domain xUnit suite covers the stable deterministic,
short-lived/credential-free attestation, mismatches of candidate
digest/contents, organization, repository, packet, generation, policy,
independent reviewers, deadlines, token expiration, and latest-read
provenance. It specifically rejects
`MayHaveTransmitted`, `OperatorReviewRequired`, a marked or
auto-retry-enabled hazard, and late changes to branch, annotated tag,
additional target ref, or newly occupied create-only candidate.

No schema migrations. Branch F-074 is stacked on F-073 PR #74 and
leaves `main` unchanged. Validate .NET10/xUnit, real PostgreSQL17/Git
integration, React/Playwright and Terraform before review.
