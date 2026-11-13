# F-075 — PostgreSQL-authoritative, read-only pre-dispatch verifier

## Purpose

F-074's pure `GitHubRemoteCreateOnlyDispatchIntegrity.Seal` verifies the
consistency of server-provided DTOs for create-only migration into a
designated GitHub test repository. It **does not** establish that the
DTOs remain the latest committed PostgreSQL authority. It also cannot
lock records against another worker transitioning F-055 to
`MayHaveTransmitted`.

F-075 introduces a deliberately **non-executable** PostgreSQL verifier
which rereads the real control-plane records while holding the
repository's coordination locks and then computes the F-074 review
receipt. No executable sender, GitHub mutation, Git credential,
production DI registration or HTTP endpoint is introduced.

## Transactional verification path

`PostgresGitHubRemoteDispatchReviewVerifier.VerifyAsync`:

1. Accepts a server-held F-044 candidate packet, F-051 ephemeral
   read-only preflight receipt, F-031 typed remote ref observation,
   F-055 hazard ID and independent reviewer actor.
2. Rejects unconfigured/restricted repositories using the existing
   **exact numeric repository allowlist** from F-055, before any SQL.
3. Opens a PostgreSQL `READ COMMITTED` transaction and obtains
   both **existing repository advisory locks in a fixed order**:
   - `pg_advisory_xact_lock(732911, hashtext(repositoryId))`, shared
     with F-045/F-047 preview lease/attempt transitions;
   - `pg_advisory_xact_lock(733050, hashtext(repositoryId))`, shared
     with F-050/F-052/F-055 fence, grant and possible-send transitions.
4. After both locks are acquired, samples **PostgreSQL server time**
   with `clock_timestamp()`, never a replica-host clock or a time
   sampled before lock contention.
5. Reads real, committed rows for the F-055 hazard and its parent
   F-052 grant, current F-050 fence, F-047 reviewed/blocked attempt and
   F-045 repository preview lease. The numeric repository identity
   scopes all lookups.
6. Requires the F-045 lease to be **Held and unexpired**, with exact
   attempt lease token, generation, actor, packet SHA, organization and
   repository. All execution/write flags must remain false.
7. Checks the append-only F-052 event history is exactly
   `Proposed -> ReviewedWriteDisabled`, and F-055 is exactly one
   `PreparedWriteDisabled` event. Missing, mutated or post-transmit
   histories fail closed.
8. Reuses the F-055 full cross-witness business integrity guard for
   all F-047/F-050/F-052 lineages, states, deadlines and two-person
   requirements. Additional assertions bind the F-044/F-051 packet,
   candidate and original policy hashes, F-050 numeric generation,
   F-047 attempt state and independent verifier actor.
9. Calls F-074's pure SHA-256 attestation **inside the same
   transaction** while the authority locks remain held; commits
   without any mutations and returns only the existing
   `ReviewedWriteDisabled` receipt.

**Lock semantics:** a competing F-055 mark or F-052 cancellation that
commits before the verifier gets the locks is seen and blocks the
review. Conversely, a mark after verification may immediately make
the receipt stale. The resulting SHA-256 review is **never** a
write-authority token, reservation or safe-to-retry signal.

## Real PostgreSQL tests

- A valid full set of F-045/F-047/F-050/F-052/F-055 PostgreSQL
  records plus append-only events produces a deterministic F-074
  receipt with all four write/auto-retry/completion flags false.
- Six concurrent replicas verify the same record without generating
  additional events or updating the hazard.
- Old/replaced/released F-045 lease, F-050 recovery, cancelled F-052
  grant, F-055 `MayHaveTransmitted` and
  `OperatorReviewRequired` all refuse the same previously valid
  review.
- Rows with missing F-052/F-055 append-only event evidence refuse
  even if the visible row state looks legitimate.
- A separate transaction holds the F-055 lock while committing a
  possible-send mark. A competing verifier must wait, observe the
  committed mark and refuse a new pre-dispatch receipt.

The test DB is PostgreSQL 17, with real migrations/constraints.
No external GitHub access is needed. No schema changes are required.

## Explicit limits before real GitHub migration

This class is **not registered in the production API** and does not
call GitHub. The F-044 packet, F-051 preflight and F-031 typed ref
readback are caller-supplied evidence. Production integration must
reconstruct F-044 from server-held persisted source/workpack evidence,
obtain F-051 and F-031 from authenticated GET-only adapters, verify
the GitHub App installation identity and readback token restrictions,
and immediately re-evaluate the locks and safety fences.

A future sender needs a separately reviewed **narrow, opt-in
destination-only GitHub writer** with destination-side compare-and-create
and proof of remote object closure. If an outcome may have transmitted,
F-055's irreversible no-auto-retry rule and human reconciliation remain
mandatory. A PostgreSQL advisory lock cannot atomically lock GitHub
state or guarantee remote write results; no production push is implied.

F-075 is stacked on F-074 PR #75; `main` remains unchanged.
CI: .NET10/xUnit, PostgreSQL17 integration, Playwright and Terraform.
