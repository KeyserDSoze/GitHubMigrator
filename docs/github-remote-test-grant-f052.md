# F-052 — Independent remote test-intent journal and no-retry observation

F-052 is a durable PostgreSQL pre-execution control plane for a **future**
GitHub create-only transfer to an explicitly approved test repository.

**F-052 cannot push to GitHub.** Its only approved state is named
ReviewedWriteDisabled; no execute API, write-capable worker, Git subprocess
or GitHub repository mutation exists in this increment.

## Disabled-by-default server configuration

- GitHubRemoteTestGrant:Enabled = false (default).
- GitHubRemoteTestGrant:AllowedRepositoryId = numeric global GitHub repo ID.
- GitHubRemoteTestGrant:AllowedRepository = organization/test-repository.

The exact repository name and ID must independently match F-050, F-051 and
the F-047 reviewed-and-blocked attempt. API callers cannot submit
credentials, remote URLs, refspecs, arbitrary Git arguments or evidence JSON.

## Four-actor review and fresh physical evidence

The original F-047 attempt needs a proposer and a distinct reviewer.
F-050 needs a proposed and independently reviewed repository-wide
future-write fence. A *new third actor*, distinct from every prior actor,
proposes the F-052 intention. A *new fourth actor*, also distinct from
every previous actor, reviews it.

Both propose and review independently call F-051. That performs the fresh
single-repository GitHub App token mint (token stays in infrastructure),
the full target branch and annotated-tag read, current inherited/ref
rulesets, and F-044 physical Git bundle SHA checks before and after network
I/O. The F-050 generation and F-047 permanent hard write barrier are
rechecked after network I/O, and F-052 *again* checks F-050 and F-047
inside the PostgreSQL transaction under F-050's numeric repo-level lock.

F-052 saves only the exact packet, candidate, F-047 policy, prior ref-set,
F-051 preflight fingerprints, and actor IDs. No bearer tokens, raw ref
names, private keys or Git commands are stored.

The F-052 expiry is the earlier of the existing F-050 review deadline and
the F-051 observation timestamp + 20 seconds. Review cannot extend it.

## Durable state model and recovery

- Proposed: one-time reserved intent for a single F-050 request/generation.
- ReviewedWriteDisabled: independently rechecked by the fourth actor,
  still no execution authorization.
- Cancelled: only the original F-052 proposer using their one-time UUID
  cancellation token and an unexpired record. DB stores only a
  domain-separated SHA-256 digest of the token.
- RecoveryRequired: after expiry, never silently available for new work.
  The transition and exactly one audit event commit before the failed
  cancellation/review returns.
- RecoveredNoWrites: a different actor confirms that F-052 physically has
  no Git transport, with the exact attestation:
  I CONFIRM F052 HAS NO GITHUB TRANSPORT AND REQUEST MANUAL NO-WRITE RECOVERY

A unique (fence_request_id, fence_generation) prevents replay even after
cancellation. A partial unique index allows only one active intention for
any global numeric GitHub target. Advisory PostgreSQL transaction locks
serialize API replicas with the existing F-050 fence. Append-only audit
events trace all state transitions.

SQL CHECK constraints enforce in every state:
execution_authorized = false; remote_writes_performed = false;
ready_for_execution = false.

When a real remote send is introduced in F-053 or later, a separate
persisted pre-transmit MAY-HAVE-SENT state and independent reconciliation
are compulsory; the no-write F-052 operator attestation MUST NOT be
reused for actual GitHub sends.

## API and roles

All endpoints are password-changed superadmin-only; the new limiter is
5 requests per minute. Base path:

/api/incremental-preparations/{prep}/operation-plans/{plan}/github-app-scopes/{scope}/transfer-attempts/{attempt}/remote-test-grants

- POST base: body has fenceRequestId, generation, expectedPacketSha256.
  Returns redacted status and a one-time cancellation-only UUID.
- POST /{grantId}/review: same body; performs fresh F-051 revalidation.
- POST /{grantId}/cancel: cancellationToken UUID, original actor only.
- POST /{grantId}/recover: acknowledgementText with exact text above.
- GET /{grantId}: redacted status bound to exact prep/plan/scope/attempt.
- GET /{grantId}/events: append-only audit, same authorization.
- **No execute, push, retry or transmission endpoint.**

## Future remote readback contract

The pure GitHubRemotePostSendIntegrity.Inspect classifier accepts a
provider-sealed complete GitHub branch/tag ref snapshot and the canonical
baseline and missing candidate sets. It compares exact tag *objects*,
not peeled commit IDs, and reports:

- ExactRemoteRefsObserved: expected full union matches, no extra refs.
- NoCreatedRefsObserved: baseline unchanged; still no retry permission.
- RemoteDivergenceRequiresOperator: partial creates, unexpected refs,
  overwritten OIDs or modified annotated tag objects.

All outcomes carry SHA-256 evidence, mayAutomaticallyRetry=false and
remoteWritesAuthorized=false. A ref-set match is readback evidence,
**not proof that a push command succeeded or that all Git objects have
been independently verified**. F-053 must add object-level checks too.

## Verification

PostgreSQL 17 multi-replica tests cover a single winning proposal,
independent reviewers, same-fence-epoch replay denial, cancellation
token binding, expiry quarantine, manual recovery and SQL no-write
constraints. Domain tests cover exact/missing/partial/divergent
annotated tags and forged provider observations. API integration
tests ensure JWT role separation and default-off behavior.

This increment does not touch main and does not merge other stacked PRs.
