# F-059 — Trusted in-process GitHub read-only verification orchestration

F-058 persists a SHA-only, non-executable reconciliation receipt. F-059
removes a crucial trust ambiguity: the **coordinator**, rather than an
external caller, invokes a trusted F-057 Git runner and passes its returned
full-object proof directly into the F-058 journal.

## Sequence and trust boundaries

1. A trusted internal caller invokes the coordinator with the existing
   **F-052/F-054/F-055 handles**, a distinct observer identifier,
   original typed refs plus create-only candidate refs, and an **ephemeral**
   installation token and issuance metadata. There is no new HTTP
   endpoint, user-supplied source path, Git URL or Git command.
2. Before any provider network read, the PostgreSQL plan reader reloads
   F-052 grant, F-054 exact tip readback, irreversible F-055
   `MayHaveTransmitted`, F-050 global target fence and F-047 blocked
   attempt. It compares repository ID, org/repo, packet/policy/candidate
   SHA and independent observer; rejects released, inconsistent or
   execution-authorizing states. An expired reviewed grant may still
   be used as historical **evidence**, never as a write permit.
3. The reader fetches the **F-029 persisted operation plan** and selects
   exactly one `TransferGitHistory` step matching the target repository.
   It then uses the existing **F-014 verified source Git-ref reader** to
   retrieve the immutable provider snapshot and SHA-bound canonical
   source refs for that plan and source repository. The local source
   mirror UUID is derived from the persisted `plan.SnapshotId`, **never
   accepted from request input**. A domain-separated SHA-256 digest seals
   all selected plan fields.
4. The coordinator calls the existing F-031 read-only provider ref
   observer using the ephemeral GitHub App token. F-052's complete-ref
   classifier rejects any observed branch/tag outside the exact
   original baseline plus candidate set, and verifies the same typed
   ref fingerprint as stored by F-054.
5. Only after these gates does F-057 execute its real read-only Git
   `ls-remote`/`fetch` into a UUID-named disposable bare mirror.
   F-056 checks the physical source mirror and downloaded target for
   full strict fsck and exact reachable commit/tree/blob/annotated-tag
   closure. In the current implementation the local physical mirror
   **must already exist** as `<trustedSourceRoot>/<plan.SnapshotId:N>.git`;
   F-059 does NOT create it or assume a staging snapshot is physical
   Git-object evidence. Missing or mismatched mirrors fail closed.
6. After the fetch, the coordinator reads the trusted source/target plan
   **again**, compares snapshot ID, full canonical source refs, source
   evidence SHA, F-054 ref fingerprint and the immutable plan seal.
   Any change refuses the receipt.
7. Only the in-process F-057 runner receipt is sent to the F-058
   journal. F-058 independently recomputes the complete remote
   object/reference proof and **under the shared PostgreSQL per-target
   advisory transaction lock** reloads all F-047/F-050/F-052/F-054/F-055
   records, audit events and no-write barriers before a single
   immutable insertion. If another replica already committed the same
   receipt, F-058's exact idempotent replay applies. Contradictions
   cannot replace a previous event.

## Configuration and deployment

All gates are **off by default**. The F-058 storage allowlist remains
independent and must be set to the exact numeric repo + `org/repo`.
An additional F-059 switch must also be enabled:

- `GitHubRemoteObjectReconciliation:Enabled=false` (default),
  `AllowedRepositoryId=0`, `AllowedRepository=""`.
- `GitHubTrustedRemoteRead:Enabled=false` (default).
- `GitHubTrustedRemoteRead:SourceRoot=""` and
  `GitHubTrustedRemoteRead:ScratchRoot=""` (both default invalid).

The coordinator and source plan reader are scoped internal services.
No API route, background sender, token minting, scheduled task, or
configuration alone invokes them. The GitHub App credential exists
only in the method's memory and ephemeral Git child environment.
There is no `git push`, no `upload-pack` server, no GitHub write API,
and no automatic retry.

## Safety and honest limitations

- Source snapshot provenance is **DB-bound to the F-029/F-014 snapshot**,
  with physical bytes checked by F-056. This does not prove a separate
  source-bundle object is equivalent unless its SHA/refs are also tied
  to this mirror. Further source-artifact linkage, independent read-only
  live GitHub.com smoke tests, LFS/submodule checks and CPU/disk quotas
  are still prerequisites for full migration verification.
- In F-059 the raw historical baseline/candidate names remain a
  trusted caller input because F-052/F-054 store **hashes only**.
  They are revalidated against the original F-052/F-054 SHA and
  provider readback. F-057 receipts are never accepted from caller input
  through the coordinator. The F-058 journal interface remains
  independently exposed to internal code; no user/API access exists.
- The snapshot can change between the post-fetch recheck and final
  F-058 commit; a future distributed source-lease mechanism should
  coordinate all source-mutating components. F-056 also performs
  pre/post fsck and source-ref revalidation, and F-058 rechecks
  the target-side safety boundaries transactionally.
- A verified remote copy **does not prove that a particular sender
  transmitted it**, and does not release or reset the F-055
  `MayHaveTransmitted` marker. No migration-complete assertion,
  push authorization or automatic resubmission is granted.
- This is an opt-in **internal composition capability**, with tests
  against provider/mirror substitutes and the existing F-057 **real
  local Git transport integration tests**. No live GitHub.com read
  test or remote write is performed by the F-059 CI.

## Tests

xUnit covers exact stage ordering, no caller-controlled receipt,
disabled mode, invalid source plan SHA, expired credentials, wrong
handles, unexpected live refs, source-plan drift after fetch,
failed Git download and cancellation.

PostgreSQL 17 integration ensures that an F-058-compatible
synthetic SHA receipt **cannot** be treated as a verified physical
source when no F-029 source plan exists; tests also cover a revoked
F-050 fence and forged F-044 create-candidate SHA. Existing F-057
Git SHA-1/256 fixture coverage and F-058 six-replica reconciliation
tests remain part of the full pipeline.
