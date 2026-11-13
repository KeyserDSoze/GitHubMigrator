# F-084 — enforce live Git ref fence and prove full Git object closure end to end

## Problem addressed

F-083 already rechecked GitHub REST refs around candidate rules and consumed
F-082's irreversible possible-send marker before calling Git. However,
another actor could change an unrelated branch/tag **after** that REST
observation and **before** the actual Git receive-pack request.
Per-ref empty `--force-with-lease` protects *candidate creation*, but not
independent unrelated refs. F-084 closes this gap **at the real Git
transport boundary**, fail-closed and without opening any public route.

## F-084 implementation

1. `GitHubSandboxPushInput` now carries the entire **typed, canonical,
   SHA-256 sealed F-051/F-083 latest baseline**, including commit/tag object
   types, as well as the existing immutable F-082 barrier and exact baseline
   SHA. The input is constructed server-side from the **second** F-083 GitHub
   GET snapshot, never from a browser or an HTTP request body.
2. `GitHubSandboxPushIntegrity.Verify` checks the full signed baseline
   (repository identity, timestamp relative to F-082, canonical refs,
   typed-ref evidence hash, F-055 baseline fingerprint and absence of every
   create candidate). Missing/modified/expired evidence is rejected before
   any subprocess or network I/O.
3. Only **after F-082 is committed**, the Git subprocess loads the verified
   physical bundle, verifies its reachable object graph and candidate OIDs,
   then invokes a **fresh `git ls-remote --refs`** on the fixed allowlisted
   origin. The observed complete `refs/heads/*` and `refs/tags/*` set
   must match the exact sealed baseline, not just candidate absence.
   Annotated tags are compared as original tag-object OIDs (not peeled
   commit OIDs).
4. Any new/deleted/diverged candidate or unrelated ref rejects the outbound
   push. F-082 remains consumed; a second send is impossible without an
   entirely new audited human review and hazard epoch. The existing F-083
   `--atomic`, empty-expected-ref leases, no-force, no-fallback,
   no-retry and token isolation remain unchanged.
5. The F-083 sender still performs F-053 post-send remote tip readback;
   any failed/uncertain readback or Git request returns reconciliation
   required, **never** migration success or automatic retry.

## What is verified by real Git in CI

The F-084 integration fixture provisions isolated Linux Git source/bundle
and destination bare repositories. It runs the **same F-083 push adapter
and Git argument list**, including `--atomic` and empty-expected-ref
leases, against a test-local bare origin selected exclusively by an
`internal` constructor; production DI cannot supply an origin and
always targets `https://github.com/{allowedOrg}/{allowedRepo}.git`.

- End-to-end SHA-1 and SHA-256: create an absent feature branch and an
  annotated tag, leave the preexisting main branch untouched, execute
  actual Git push, verify all target refs and `git fsck --strict`.
- An **independent F-057 smart Git fetch**, using a NEW disposable bare
  mirror, followed by F-056 full reachable-object closure comparison of
  all reachable commits, ancestors, trees, blobs and annotated tags. All
  source/remote object set hashes match and no scratch files remain.
- A **candidate** created by a competing actor, and an **unrelated**
  branch added after REST preflight, each fail at the real Git ref fence
  without invoking push or overwriting either ref. Retries after both a
  successful push and refused drift are blocked by the same one-shot guard.
- Domain tests reject altered baseline SHA, forged typed evidence, stale
  observations and baseline refs containing a create candidate.
- Existing F-082 PostgreSQL 17 tests prove that the real durable possible-send
  marker and its audit commit *before* the F-083 adapter can be invoked,
  across six concurrent replicas.

## Security and production boundary

The local origin exists **only in the test assembly and the internal test
constructor**. The production instance never allows caller-supplied
remote URLs or Git arguments. The sender remains disabled by default,
not reachable from an HTTP route, worker or queue. No GitHub.com push is
performed by CI, and we do not claim a real GitHub remote acceptance test.

The full closure proof uses existing F-056/F-057 verified code; it is
not a claim that F-083's immediate F-053 response constitutes a persisted
independent F-058 closeout. F-085 should link the sender's readback to
the F-054 and F-059/F-058 durable independent reconciliation workflow
and include a controlled *real* GitHub sandbox exercise before any
production activation.

F-084 is stacked on F-083. `main` remains unchanged.
