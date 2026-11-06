# F-046 — per-ref GitHub rule inspection + ambiguous-transfer outcome guard (GET only)

F-046 is a **read-only, transient policy review** for the exact CREATE-only
refs physically validated by F-044. It does **not** mint a GitHub App token,
push a commit, create a ref, interpret a GitHub rules response as effective
permission, or authorize a transport.

GitHub documents that \`GET /repos/{owner}/{repo}/rules/branches/{branch}\`
returns active rules **even for branches which do not yet exist**:
https://docs.github.com/en/rest/repos/rules#get-rules-for-a-branch

## API

Superadmin + password-changed JWT:

\`POST /api/incremental-preparations/{prep}/operation-plans/{plan}/github-app-scopes/{scope}/create-only-packet/candidate-policy\`

\`\`\`json
{ "expectedPacketSha256": "<exact F-044 SHA>" }
\`\`\`

The input contains no ref names, API tokens, push arguments, URLs, local
filesystem paths or mutable target selection. The service regenerates F-044
from the complete PostgreSQL reconciliation and re-hashed physical Git
bundle. For safety this initial online probe is bounded to **1–16 CREATE
candidates**; larger workpacks fail with
\`CandidatePolicyOnlineBudgetExceeded\` instead of exhausting GitHub API
requests. A separate 6/minute server limiter also protects the provider.

## What is checked

1. Verify the current F-043 scope/F-042 30s remote fingerprint/F-040
   two-person intent/F-041 real Git rehearsal/F-038 physical source
   bundle and F-044 packet before accessing the destination token.
2. Read only the original server-held destination **read** credential,
   distinct from the separate F-043 GitHub App PEM (its installation token
   was discarded after attestation).
3. Fixed GitHub REST host and GET-only endpoints:
   - \`GET /repos/{org}/{repo}\` must report the **same numeric repository
     ID**, owner/full name and a live non-archived, enabled repository.
   - \`GET /repos/{org}/{repo}/rulesets?includes_parents=true&per_page=100\`
     examines inherited active tag/push restrictions.
   - \`GET /repos/{org}/{repo}/rules/branches/{branch}?per_page=100\`
     examines effective active branch rules, including nonexistent branches.
4. At most four concurrent branch GETs, 65KiB maximum body each, at most
   99 elements per response; no redirects, no cookies, strict JSON parse,
   recursive duplicate-key rejection and mandatory pagination completeness.
5. A non-empty effective branch rules response, active inherited tag ruleset
   or active inherited push ruleset is **\`RulesRequireReview\`**.
   A complete empty result becomes **\`NoActiveRulesReported\`**, *never*
   \`Contents:write\`, push capability or execution permission.
   Any 403/404/429/redirect/invalid JSON/ambiguous pagination, numeric
   repo change or unknown ruleset shape is **\`PolicyUnverified\`**.
6. Re-read F-044 and recheck its physical bytes/freshness after all GitHub
   GETs. A stale 30s fence rejects the entire review. Produce a versioned
   SHA-256 digest binding the exact packet and per-ref categorical results.
   Ref names are exposed only as **domain-separated SHA-256 digests**, not
   in the review response. The full F-032 workbench remains the human
   location for readable ref names.

Even \`AdvisoryNoRulesReported\` leaves
\`writeCapabilityVerified=false\`, \`executionAuthorized=false\`,
\`remoteWritesPerformed=false\` and \`readyForExecution=false\`.
This phase is **not** a dry run of GitHub's Git network transport and
cannot prove that protected branches, bypass actors, legacy tag rules,
hook requirements or org policy allow a future push. All REST calls
remain GET.

## Unknown Git transport outcomes

A separate pure \`GitHubRemoteWriteOutcomeGuard\` models future worker
outcomes. No Git transport is implemented in F-046. A definite
pre-transmission refusal is \`NoRemoteWriteAttempt\`; timeout after
transmission, connection reset, server failure, partial acceptance and even
reported success are **\`RequiresRemoteReconciliation\`** until independently
re-reading the remote refs and their exact object IDs. Unexpected refs
are \`RemoteDivergenceBlocked\`. **Every outcome has
\`mayAutomaticallyRetry=false\`** and no outcome permits execution.

Before enabling the first test-repository remote push: implement an
independent run-authority grant, effective per-ref App identity checks,
target-wide durable *write* coordination separate from the F-045 preview
lease, compare-and-create semantics in Git transport, quarantine/repair
for ambiguous outcomes, and independent post-write SHA verification.

## Tests

Mocked GitHub HTTP verifies exact GET paths/hosts, credential confinement,
non-existent branch rules, inherited tag/push rulesets, numeric repo ID,
recursive duplicate JSON, 403/404/429/301, oversize, incomplete pagination,
strict 16-candidate cap, categorical SHA sealing and no-write flags.
ASP.NET/PostgreSQL integration checks superadmin-only access and missing
SHA-bound evidence denial before any GitHub request.
