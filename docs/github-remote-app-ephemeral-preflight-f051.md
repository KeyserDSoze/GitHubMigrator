# F-051 — scoped GitHub App remote preflight (GET only)

F-051 is the first *remote GitHub* transport-adjacent adapter. It
re-mints a strictly single-repository GitHub App installation token in
private infrastructure code and consumes it for read-only remote
observations. It does **not** run git push, start any Git subprocess,
create a ref, or change any repository state.

The only non-GET network call is GitHub's own token issuance:
\`POST https://api.github.com/app/installations/{id}/access_tokens\`.
It requests precisely one repository and \`contents:write\`, with
GitHub's implicit \`metadata:read\`. The response must prove
\`repository_selection=selected\`, exactly one repository with the
correct owner/name/numeric ID, no extra permission, and a bounded expiry.
A follow-up GET to GitHub's repository metadata must confirm the numeric
ID, owner and that the repository is not archived or disabled.
A token is returned *only inside* the infrastructure implementation,
never via an API, domain receipt, persistence entity or log.

## Opt-in requirements

All predecessor flags and attestation prerequisites must be enabled and
current. F-051 has no independent execution grant:

- F-043 App options: enabled, fixed App ID, installation ID, and
  protected private-key secret reference; fixed \`organization/repo\`
  allowlist.
- F-050 global remote write coordination: enabled, exact numeric GitHub
  repository ID plus matching \`organization/repo\` allowlist, current
  \`ReviewedWriteDisabled\` request and generation.
- F-044 current, physically verified read-only packet, exact SHA supplied
  as a client precondition. F-044 is recomputed *both before and after*
  all GitHub GETs.
- F-047 two-person reviewed hard write barrier remains closed;
  F-050 writes are still permanently denied by database constraints.

The endpoint is superadmin-only with a standalone **4/minute** rate
limit. By default the required F-043 and F-050 flags are both off.
No user-provided URL, token, private key, Git command or refspec can be
supplied.

### API

\`POST /api/incremental-preparations/{prep}/operation-plans/{plan}/github-app-scopes/{scope}/remote-sandbox-write-fence/{requestId}/app-preflight\`

Body:
\`\`\`json
{
  "generation": 1,
  "expectedPacketSha256": "<64-character F-044 SHA>"
}
\`\`\`

Response is a SHA-256-sealed
\`RemoteReadOnlyAppPreflightVerified\` receipt containing only the
request/generation, numeric destination ID, SHA fingerprints, safe
ref counts, observation timestamps and three explicitly **false**
\`remoteWritesPerformed\`, \`executionAuthorized\`,
\`readyForExecution\` booleans. There is **no token, private key, URL,
raw ref name or execution permit** in the response.

## Independent GitHub verification

Within one ephemeral token session the adapter:

1. Re-verifies the token and repository using a fixed-host metadata GET.
2. Reads the **entire** target branch and tag ref sets via the GitHub
   Git-data \`matching-refs/heads\` and \`matching-refs/tags\` endpoints.
   Annotated tags retain the tag object's exact OID, not a peeled
   commit OID. A 404 or pagination uncertainty is never treated as an
   empty repository.
3. Checks the fresh full ref-set fingerprint against F-042's prior
   stable baseline. Any new target-only ref, altered OID, deleted ref,
   changed annotated-tag object or candidate name appearing early
   makes the receipt fail closed.
4. GETs current inherited/global rulesets and effective rules for
   each *absent* branch candidate, with the F-046 16-candidate budget.
   Active restrictions and ambiguous responses fail; **no reported
   rules is only an advisory observation, never permission to push**.
5. Recomputes F-044 physical bundle evidence and re-reads F-050's
   generation/state after all provider operations. A stale/changed
   fence, target ID, prior scope or packet SHA fails closed.

All HTTP requests are to fixed \`https://api.github.com\` paths with
redirects/cookies disabled, response-size limits, 18-second overall
deadline, and no fallback to a PAT or generic OAuth credentials.

## Security/operational boundary

F-051 explicitly does **not** claim GitHub write capability or imply
that a push will succeed. The App token has limited \`contents:write\`
scope because later Git transport will need it, but F-051 uses it
solely for GETs, discards it after the call and never transports it
into a Git subprocess.

Next: F-052 must introduce a separately authorized, **named test
repository** execution grant, durable one-time potential-send journal
and independently reconciled remote Git object transport. Production
remote writes stay disabled until those requirements are implemented
and a test repository is explicitly approved.
