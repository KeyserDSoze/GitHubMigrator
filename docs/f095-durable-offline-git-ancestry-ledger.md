# F-095 — Durable offline ancestry evidence ledger

F-095 joins the F-094 physical offline Git ancestry verification with
the existing **verified** PostgreSQL F-091 source checkpoint, F-032/F-014
immutable original source Git ref/OID manifest, and F-031 destination
GitHub ref observation. This is intentionally an **internal-only,
disabled-by-default** infrastructure capability: there is no F-095
public HTTP endpoint, autonomous worker, GitHub credential or write action.

## Security and evidence boundaries

The ledger takes GUID handles plus a source repository identifier and
expected destination SHA-256. It does **not** accept a user-supplied
list of source or destination OIDs or a user-supplied local path. On
capture, it re-reads:

1. An F-029 immutable plan and its current reviewed source→target mapping.
2. The corresponding F-091 *version 2* checkpoint, including a complete
   reread of the historical F-025 source delta digest and Recorded event.
3. The immutable F-032 source Git ref/OID proof from sealed F-014 chunks.
   Partial and legacy-inline inventories are rejected in F-095 v1.
4. The independently sealed F-031 destination observation. It must be
   current, from the same plan/repository mapping, later than the source
   staging commit, and bound to the exact expected destination SHA-256.
5. The F-094 read-only physical Git ancestry receipt, comparing the
   **exact ref/OID arrays** proven by items 3 and 4.

The offline F-094 Git subprocess is sandboxed, cannot fetch/push, checks
full object closure and refuses missing/shallow/promisor/alternate state.
F-095 never equates a staging fingerprint with a Git object ID.

After the physical audit, F-095 acquires the same source-scoped
PostgreSQL advisory lock used by plan/observation/checkpoint capture,
re-reads the authoritative evidence and then inserts a single immutable
receipt plus its \`Recorded\` event in the **same transaction**. A unique
index makes exact retries idempotent. A changed physical proof for the
same plan/checkpoint/observation/repository key fails closed.
Triggers prevent updates/deletes, constraints reject non-false remote
write/dispatch/retry/completion flags, and a restart read re-verifies
the original referenced proofs and Recorded event.

A once-valid F-095 receipt remains historical evidence when its F-031
observation expires. Attempting a **new** capture with stale evidence
is rejected; nothing promotes a historical receipt into live authority.

## Scope of this increment

- Supports **only the F-029 baseline snapshot** with a complete,
  independently sealed F-014 ref manifest. F-090 later incremental
  snapshots are not yet supported for F-095 physical OID binding.
- The physical F-094 source handle presently resolves to a
  \`<sourceSnapshotId>.git\` bare directory under a configured root.
  F-060 creates workpack-specific physical mirror IDs. A future
  adapter must bind the correct F-060 verified mirror identity and
  lifecycle to the F-094 object proof before F-095 can be used across
  the deployed mirror fleet. Do not claim this join is complete.
- The local target bare repository is a **sandbox copy**. F-031
  observations are historical, not a live push-safety assertion.
  No GitHub.com live object readback is performed by F-095.
- There is **no enabled production automation**: both
  \`GitIncrementalAncestryLedger:Enabled\` and
  \`GitIncrementalAncestryLedger:PhysicalAuditEnabled\` default to
  \`false\`. The deployment must configure safe, owned
  \`SourceRoot\` and \`TargetRoot\` paths before enabling either.
- A matching OID or local fast-forward candidate remains
  **non-executable**. All \`CanPush\`, \`CanRetry\`, \`CanDispatch\`
  and \`MigrationComplete\` fields are false.

## Testing

The .NET unit tests check every evidence binding (including forged
source/target OIDs, foreign checkpoint and mismatched SHA). PostgreSQL
17 integration tests cover exactly-once replay, immutable row and
event triggers, SQL authority constraints, restart reads, stale target
observations, a disabled ledger and partial-inline-source refusal.
These tests use isolated trusted-port stubs to exercise the PostgreSQL
join; the physical SHA-1/SHA-256 subprocess and graph scenarios are
independently covered by the F-094
\`LocalGitIncrementalAncestryAuditorTests\`.

**F-096 implemented:** new F-095 captures now require a verified
workpack-specific F-060/F-061 mirror (production default). The ledger
derives the mirror ID from `GitSourceSnapshotMirrorIntegrity.PhysicalMirrorId`,
recomputes the original F-060 SHA-256 from the F-035 bundle row,
verified F-014 ref set and persisted F-061 object digest/count.
It also recomputes the F-032 ref/OID reconciliation SHA and decisions
against the exact F-031 observation, requires the linked F-038 workpack
to be blocked at the destination-write barrier, and checks the latest
`Verified` F-061 lifecycle event and lease state.
The F-094 physical auditor must read the derived `<physicalMirrorId>.git`
rather than the logical `<snapshotId>.git`, then match the same complete
object-set digest before and after fsck. Both mirror ID and F-060 proof
are covered by the new version-2 F-094 ancestry SHA. The ledger checks
this again under the PostgreSQL source lock before recording.

The pre-F-096 v1 receipts remain verifiable and retain their original
hash format. Production DI always requires the new mirror bridge;
legacy unbound capture exists only in isolated historical regression
tests. F-096 is still an internal, off-by-default read-only capability.
An F-060 object-set proof is NOT a live GitHub remote attestation.
No additional GitHub transport or write authority was introduced.

**Remaining for F-097:** produce complete immutable F-014-style ref/OID
evidence for every later F-090/F-091 incremental snapshot, rather than
using only its per-ref delta fingerprint. Audit records are not
substitutes for live F-031 freshness or an execution permission. A
versioned production schema-migration rollout is also still required.
