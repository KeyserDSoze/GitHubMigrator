# F-097c.4c.0a — Verify later checkpoint target re-review prerequisites

Child [#106](https://github.com/KeyserDSoze/GitHubMigrator/issues/106)
of [#105](https://github.com/KeyserDSoze/GitHubMigrator/issues/105).
Checkpoint-specific F-031 capture and full source-locked workpack
#104/#103/#100 remain **unfinished**.

## Why this is separate from legacy F-031

F-029 has a deliberate safety requirement: when a newer F-025 source
snapshot arrives, the original F-029 plan becomes `IsCurrent=false`.
The original F-031 target evidence writer refuses to seal any new
observation using that historical plan. Real two-epoch SHA-1 and
SHA-256 PostgreSQL tests proved this. Changing the flag would allow a
stale reviewed mapping to authorize destination operations.

## Internal, non-executable candidate verifier

`PostgresGitCheckpointTargetReReviewPrerequisites.ReadAsync` is
off by default. It **never calls a GitHub provider**, reads credentials,
writes F-031, or queues any work. The caller supplies only the
identifiers of an independently persisted F-029 review and latest
F-091 checkpoint, source repository and operator. The service replays
the complete stored F-097b source Git ref/OID evidence chain and
verifies the source-selected checkpoint BEFORE acquiring a write
transaction.

Inside the existing *source-first* PostgreSQL advisory lock, the
service rechecks the last completed source staging sync, latest
F-091 checkpoint, immutable F-097 full-ref proof identity, original
F-029 manifest and its single reviewed source-to-GitHub mapping,
historical human F-027 approval event, unchanged source and
destination validated connection identities, and destination
validation age (at most five minutes). It does not pretend the
historical F-029 is current. Any missing, superseded, foreign,
unreviewed, expired or tampered evidence is refused.

`GitCheckpointTargetReReviewIntegrity.Seal` makes a deterministic,
versioned SHA-256 digest of source snapshot/checkpoint/F-097 full
ref root, baseline F-029 review/mapping, original reviewer and
candidate operator. The record deliberately contains NO credentials,
network URLs or target Git OIDs. All five capabilities
(`CanObserveTarget`, `CanPush`, `CanRetry`, `CanDispatch`,
`MigrationComplete`) are **false**, always. It is NOT an
authorization to make an external request.

## Next boundaries

A *new independent checkpoint-scoped human review/approval*, followed
by a fresh, credential-scoped read-only GitHub GET, and a distinct
immutable **checkpoint-specific F-031 observation** still must be
designed, persisted and verified under the source lock in #105.
Only then can #104 and #103 create a later-checkpoint F-032/F-038
and F-035 source-only bundle without violating historical F-029
integrity. No remote GitHub push or migration-complete authority.

## Integration and safety regression coverage

The F-097 full-ref proof is not inferred from F-091 staging fingerprints.
The real PostgreSQL integration fixture records the F-014 and F-091
**baseline** and an independent, completed later F-014 snapshot with
its own SHA-1 or SHA-256 ref/OID set, then seals the F-097 predecessor
chain. The old F-029 plan remains `IsCurrent=false`.

Scoped tests check that the candidate can be computed on the later
snapshot without inserting any F-031/F-032/F-038 rows, that re-opening
the database yields the same source snapshot, and that disabled mode,
wrong operator, foreign repository, superseded *baseline* F-091
checkpoint, expired destination validation, revoked historical review,
and tampered proof authority bits fail closed.

The candidate's `CanObserveTarget=false` is intentional and permanent.
A new explicit reviewer decision and actual read-only GitHub target
observation are **not** covered by these tests, nor by the receipt.
