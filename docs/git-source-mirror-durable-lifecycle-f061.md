# F-061 — durable PostgreSQL lifecycle and fenced leases for physical source mirrors

F-060 materializes complete Git object stores from previously reviewed
F-038 bundles into unique, UUID-named local bare mirrors. F-061 adds
**durable admission, evidence persistence, crash recovery and quarantine**,
without making any local proof a GitHub write authorization.

## Operational path

`F-029/F-014 source plan → F-038 verified bundle → F-061 PostgreSQL
lease → F-060 atomic local Git materialization and F-056 full-object
verification → F-061 Verified checkpoint → F-059/F-057 GitHub
read-only object fetch → F-058 immutable remote reconciliation`.

The F-059 DI composition wraps the existing physical F-060 materializer in
`DurableGitSourceSnapshotMirrorMaterializer`. No new HTTP route, worker,
outbox, GitHub credential, write authorization or scheduled job is
introduced. All three relevant switches are OFF by default:
`GitSourceBundles:Enabled`,
`GitHubSourceSnapshotMirror:Enabled`, and
`GitHubSourceMirrorLifecycle:Enabled`. The F-059 coordinator and
F-058 remote read-only audit each require their own independent
default-off switch.

## Durable states and concurrency

- **Leased**: a PostgreSQL row binds the deterministic
  `PhysicalMirrorId=SHA256(F-029 snapshot UUID,F-038 workpack UUID)`
  to its immutable plan/reconciliation, source evidence SHA, full F-038
  bundle SHA and complete Git refs SHA. It records a random,
  per-attempt fencing token, random worker ID, generation number and
  15-minute expiry. Neither tokens nor worker IDs are Git credentials.
- **Verified**: only the worker holding the current, unexpired
  generation/token may persist F-060's cryptographically rechecked
  proof SHA, complete bounded Git object-set SHA and count.
  Re-verification of the same artifact requires a fresh lease and
  must produce the same original object digest and proof hash.
  A status read alone is not permission to skip physical re-checking.
- **Expired Leased → Leased (LeaseReclaimed)**: after a worker crash
  a different replica may claim a *new generation*. The previous
  token is rejected forever. The replacement MUST rehash the F-038
  bundle and re-run F-060/F-056 on any already promoted final mirror;
  no unverified or half-written temporary directory is promoted.
- **RecoveryRequired**: on corrupted physical source, bad original
  bundle or conflicting proven object hash, the still-owned lease
  is quarantined. Automatic acquisition and retry are refused.
  Manual investigation and a separately reviewed operator recovery
  flow are future work. The original verified digest remains for audit.

Acquisition, commit and quarantine each take a PostgreSQL advisory
transaction lock keyed by the **physical mirror UUID** and re-check
the row and current generation before altering state.
The table enforces one physical mirror per F-038 workpack ID, monotonic
generations capped at 2000, SHA-256 syntax, matched lease-state fields,
and permanently false remote push, remote write authorization,
automatic retry and migration-complete flags.

The separate event table stores only
`LeaseAcquired`, `LeaseReclaimed`, `ReverifyStarted`,
`Verified` and `Quarantined` entries. It forbids direct
UPDATE/DELETE via PostgreSQL trigger. Neither ref names, filesystem
paths, tokens, Git credentials nor remote URLs are stored in events
or returned in the public-facing status shape.

## Crash orphan cleanup

F-060 gains an **explicit, operator-only** method for cleaning staging
directories after a process crash. It operates only under the existing
cross-process filesystem lock and only if the directory name matches
`<32-hex-physical-id>.<32-hex-random-id>.tmp`, is older than
**24 hours**, and contains no symlinks/reparse points. It processes
at most 5000 matching entries and removes at most 40 per call.
Final `<physical-id>.git` mirrors are NEVER deleted or replaced.
Unrecognized temporary names and bare-looking `.git` directories are
retained rather than risk deleting a still-owned artifact.
There is no automatic cleanup task or user-facing deletion endpoint.

## Security and reliability limits

- The database coordinates **worker admission and receipt fencing**.
  F-060 also uses a POSIX file lock to serialize actual imports among
  processes sharing a single lock-capable volume. The database does
  not prove that workers on distinct hosts share the same physical
  volume or coordinate arbitrary OS-level writers.
- A 15-minute lease is deliberately bounded. No automatic heartbeat
  or lease renewal is present: a long verification must fail its SQL
  acknowledgement when its lease expires, and a successor must
  reverify the physical mirror. Lease expiry is never treated as
  execution authorization.
- The F-061 hash-only lifecycle record is **not a standalone
  attestation of the original F-038 database lineage**. F-059's
  trusted PostgreSQL plan reader supplies and revalidates the
  F-029/F-014/F-038 source proof; F-061 binds its immutable fields.
  Additional direct F-038 composite foreign keys and a dedicated
  durable source-workpack lock are future hardening work.
- The status exposes a historical *Verified* checkpoint, not proof
  that an operator has not subsequently modified a Git object.
  Every actual reuse goes through a fresh F-060 verification under
  a new lease.
- Schema initialization is additive; production deployment requires
  a reviewed, versioned EF/PostgreSQL migration. Database-level
  CHECK constraints and audit event immutability are applied even
  if EF creates the tables before application startup.
- Live GitHub.com HTTPS read-only smoke tests, LFS and submodule
  content checks, disk/cgroup budgets, independent-source lease
  fencing during the entire remote fetch, and remote execution
  approval remain separate work. F-055's possible-send hazard
  is never released or automatically retried.

## Tests

- xUnit: physical source proof SHA/age/identity tampering, no-Git
  without a lease, and corruption quarantine.
- PostgreSQL 17 + real Git: six independent replicas competing for
  the same lease, one successful import, a post-promotion simulated
  crash and new fencing generation, stale-worker rejection,
  three-worker Verified re-verification, corrupt pack quarantine,
  conflict with original object-set digest, and SQL attempts to
  authorize writes/retries or erase append-only audit.
- Local Git integration: only recognized 24-hour-old `.tmp` staging
  is cleaned; current temps, final `.git` and unknown directories
  survive, and symlinked staging is rejected.
- CI runs the full .NET, PostgreSQL, React/Playwright and Terraform
  quality gates. No GitHub push occurs.
