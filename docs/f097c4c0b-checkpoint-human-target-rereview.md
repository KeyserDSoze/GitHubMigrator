# F-097c.4c.0b — Immutable, independent human review of later source checkpoints

Tracked in [#107](https://github.com/KeyserDSoze/GitHubMigrator/issues/107),
child of [#105](https://github.com/KeyserDSoze/GitHubMigrator/issues/105)
and prerequisite to later F-031/F-032/F-038 source workpacks (#104/#103).

## The boundary matters

A real later completed F-014/F-091/F-097b source Git discovery
**invalidates the old F-029 plan's IsCurrent flag**. We do not
resurrect it, relabel an older F-031 target observation, infer source Git
OIDs from F-091 fingerprints, or reuse an earlier destination approval.
Issue #106 provides a candidate derived from independently verified
immutable historical mapping and latest source ref/OID evidence.

Issue #107 adds **a separate explicit human decision** for the exact
checkpoint, source repository and target repository name:

- `GitCheckpointTargetHumanReviewIntegrity` creates a versioned,
  SHA-256 domain-separated review receipt from the #106 candidate SHA,
  exact source checkpoint/snapshot and destination ID, source requester,
  a **different reviewer**, decision, time and permanently false flags.
  `ApprovedForObservation` and `Rejected` are the only decisions.
- `PostgresGitCheckpointTargetHumanReviewJournal` is **off by
  default**, internal-only, and never registered as an HTTP endpoint
  or worker. It obtains a fresh #106 source-locked candidate before
  acquiring its source-first PostgreSQL advisory lock, then checks
  latest staging/checkpoint/immutable F-097b ref proof, historical
  F-027 approval, unchanged connections and destination validation
  age again before committing.
- A new append-only decision row and one immutable audit event are
  inserted in the **same database transaction**. A replay with the
  same reviewer, candidate and decision verifies the receipt and audit
  event, returning the old receipt. Another reviewer, alternative
  decision or different candidate is a hard conflict. SQL CHECKs
  prohibit observation, push, retry, dispatch and completion authority;
  triggers reject UPDATE and DELETE on both tables.
- A rejection is terminal for that checkpoint/repository pair. A new
  review requires a *new source checkpoint*, never an overwrite.

## Still not authorized

Even an `ApprovedForObservation` receipt has
`CanObserveTarget=false`. This is **not** a permit to access GitHub.
The journal accepts a reviewer identifier from its internal caller; a
later authenticated superadmin/reviewer API must bind that identifier
to the real security principal before such decisions can be accepted
from end users. No credentials, HTTP GET, GitHub push or sender path
is registered by this work.

The parent [#105](https://github.com/KeyserDSoze/GitHubMigrator/issues/105)
must implement the authenticated checkpoint-specific reviewer API,
source-locked fresh **read-only GitHub GET**, and an independently sealed,
checkpoint-scoped F-031 target observation. Only afterwards can
[#104](https://github.com/KeyserDSoze/GitHubMigrator/issues/104)
capture durable F-032/F-038 workpacks for an actual later epoch,
followed by F-035 and F-060/F-061 integration (#103/#100) and F-095
physical v3 ancestry (#96). Parent #92 remains open.

## Tests

Domain SHA1/SHA256 checks cover deterministic digests, independent
reviewer, evidence tamper, swapped checkpoint and permanent false
authority flags. Real two-epoch F-014/F-091/F-097b PostgreSQL tests
cover both `ApprovedForObservation` and `Rejected`, idempotent
replay from separate DbContexts, concurrent same-decision requests,
foreign reviewer/decision conflicts, SQL mutation guards and expired
destination validation. They also assert no new F-031/F-032 rows and
the original F-029 plan stays non-current.

The code never creates an operational workpack, provider authorization,
migration-complete receipt or destination GitHub write.
