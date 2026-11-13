# F-086 — persist an immutable typed pre-send GitHub ref baseline

## Why this is the next necessary step

F-085 provides a restart-safe recovery projection and a hash-only
checkpoint of F-082, F-054 and F-058. F-059 can independently acquire
remote objects and store F-058, but its trusted reader requires the
**original typed baseline** (branch/tag names, object IDs and
`commit`/`tag` types) and the original create-only candidate refspecs.

Earlier F-051/F-081/F-082 journals persist their SHA-256 fingerprints, not
those full typed inputs. A worker that crashes after F-082 cannot safely
rebuild them by reading the current remote: the current remote may now
contain the very refs it was about to create. Asking an operator to
reenter these refs, or assuming the remote is still the original
baseline, would invalidate independent recovery.

F-086 captures the missing historical inputs **before F-082 commits**,
without granting any send authority.

## Implementation

`PostgresGitHubRemoteTypedRecoveryBaselineJournal.CaptureAsync` takes
the exact F-081 confirmation, F-080 intent and preparation/plan/scope
identities, the independently acting F-083 operator, the rebuilt F-044
create-only packet and the **last typed GitHub GET snapshot** (after the
two F-083 provider GETs and policy checks).

Under existing ordered repository advisory locks **732911 → 733050**
it verifies F-081 still current, F-055 still
`PreparedWriteDisabled`, F-052 reviewed grant and exact packet/candidate
and baseline fingerprints. The separately configured
`GitHubRemoteTypedRecoveryBaseline` allowlist must be explicitly
enabled for the same numeric sandbox repo and `org/repo`. A snapshot
cannot be captured by the confirming/preparing/reviewing operators.

The inserted F-086 record and unique audit contain only the original
typed branch/tag refs and candidate OIDs as bounded JSON, immutable
lineage IDs, canonical SHA-256 digests and the independent human.
No token, private key, HTTPS URL, Git command or raw object is stored.

**The F-083 sender now requires a successful F-086 capture before it
can commit F-082 and before it can invoke the Git sender.** If this
fails (disabled, DB outage, review expired, scope mismatch, drift or
duplicate snapshot), F-082 remains unconsumed and there is no push.
This is intentionally fail-closed; explicitly enabling F-083 without
also enabling F-086 does not authorize a send.

## Crash recovery

`ReadAfterBarrierAsync` performs a strictly scope-bound read against
the persisted F-081 confirmation and verifies a matching F-082
`MayHaveTransmittedWriteDisabled` barrier for the SAME actor, hazard,
grant, packet and candidate SHA. It also checks the irreversible F-055
marker before returning the snapshot.

If F-082 was never committed, even a recorded F-086 snapshot cannot
be used to claim a possible send. After F-082, a new process can
reconstruct exact typed refs and candidates from the F-086 row,
recompute the original GitHub observation digest, and verify the
immutable F-086 event. Neither process can rewrite the baseline
following remote changes.

`GET .../confirmations/{confirmationId}/typed-recovery-baseline` is a
scoped, rate-limited, password-changed superadmin-only status route. It
returns only GUIDs, SHA-256 hashes, counts and permanent false
send/retry/completion flags; it never returns branch names/OIDs to an
HTTP client. The route and store are disabled by default.

## Tests

- SHA-256 domain tampering across all lineage IDs, typed annotated-tag
  object types, OIDs and candidate set and forged execution/retry flags.
- Full real PostgreSQL F-081 → F-086 → F-082 chain; a fresh database
  connection recovers exact original typed baseline and candidate refs.
- Snapshot captured before F-082 is invisible to post-send consumers
  until F-082 has committed.
- Six concurrent replicas on the same repository: one snapshot and
  unique event; five refused attempts.
- Wrong actor, wrong plan/scope, forged typed refs and a stale F-082
  hazard are rejected.
- SQL UPDATE/DELETE of refs, SHA or audit is rejected by PostgreSQL.
- F-083 integration with an injected capture stub verifies the capture
  occurs before marking F-082, and a capture failure prevents any push.
- API JWT/RBAC and disabled-by-default no-network test.

## Explicitly not done by F-086

F-086 does **not** invoke F-054 or F-059 automatically. It supplies
their missing trusted historical source data so a later independent,
read-only recovery operator can do so after a crash, with a fresh
one-repository **Contents:read** GitHub App installation token.
The existing F-083 remote sender is still sandbox-only and has **no
public execution route**. CI performs no real GitHub.com push.

This PR is stacked on F-085. `main` remains untouched.
