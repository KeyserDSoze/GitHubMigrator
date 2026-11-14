# AI conversion: model connections and workflow gates

Status: **model connections, gates, a converter that returns a *proposal*, the kept proposals, the human approval, a read-only *publication plan* and an
opt-in, allow-listed publisher of the pull request are implemented. The publisher is OFF by default and has only been exercised against the GitHub
simulator.** The conversion itself never approves or publishes anything. See
[ADR-011](../ADR/ADR-011-ai-conversion-gates-and-model-connections.md), [ADR-012](../ADR/ADR-012-kept-proposals-and-human-approval.md) and, for the Azure triggers,
[ADR-015](../ADR/ADR-015-azure-triggers-as-a-separately-approved-derived-proposal.md) and, for replacing a published file and checking an attempt against the destination,
[ADR-016](../ADR/ADR-016-replacing-a-published-file-and-reconciling-an-attempt.md).

## Model connections (UI: Connections → *AI model connections*)

| Setting | Meaning |
| --- | --- |
| Provider | `OpenAICompatible` (OpenAI, vLLM, Ollama, LiteLLM, LM Studio: `POST {endpoint}/chat/completions`, Bearer) or `AzureOpenAI` (v1 route `/openai/v1` without an API version, deployment route `/openai/deployments/{model}/…?api-version=` with one; `api-key` header) |
| Endpoint | normalised and checked against the operator allow-list before anything is sent |
| Model | model id, or the Azure deployment name |
| Authentication | `ApiKey` (stored encrypted in the connection secret store), `None` (local server), `ManagedIdentity` (Azure OpenAI only, and only when `Ai:AllowManagedIdentity=true`) |
| Advanced | max output tokens, token parameter (`max_tokens` or `max_completion_tokens`), temperature (or omit it for reasoning models), timeout |
| Source consent | `SendSourceContent`: a `superadmin` decision, recorded with who/when, only on a verified connection |

Operator configuration (never user input): `Ai:AllowedHosts` (comma separated, `*.` suffix wildcards; default
`*.openai.azure.com`, `*.cognitiveservices.azure.com`, `*.services.ai.azure.com`), `Ai:AllowHttp` (Development only),
`Ai:AllowManagedIdentity`. In Compose: `AI_ALLOWED_HOSTS`, `AI_ALLOW_MANAGED_IDENTITY`; `./script/dev.sh lab` allows the
`llm-sim` host.

API (`superadmin,admin`; consent is `superadmin` only): `GET /api/ai-connections`, `GET …/options`, `POST …/validate`
(tests a draft without saving), `POST /api/ai-connections`, `POST …/{id}/verify`, `PUT …/{id}/credential`,
`PUT …/{id}/source-consent`, `DELETE …/{id}`. Responses never carry the key or the secret reference.

Verification codes: `Verified`, `Unauthorized`, `ModelNotFound`, `RateLimited`, `Timeout`, `Unavailable`,
`MalformedResponse`, `ContentFiltered`, `ContextLengthExceeded`, `UnsupportedParameter`, `EndpointNotAllowed`,
`CredentialMissing`, `NotVerified`.

## The converter (`POST /api/workloads/pipelines/convert`, UI: Pipelines → Actions → *Propose a conversion with AI*)

Body: `{ sourceConnectionId, projectId, pipelineId, aiConnectionId, maxRepairs? }` (`admin`/`superadmin`, rate limited). Answer: a
**proposal** (`proposalOnly: true`; `saved` / `proposalId` / `proposalState` say it was kept for review, see below) with the workflow, the gate results, the plan and the provenance. Never the
source YAML.

1. Read the pipeline metadata (variables with secret flags, service connections, task structure) with the existing preview reader.
2. Resolve the AI connection: verified, host still allowed, key present, **source consent given**. If not, the result is
   `NotAttempted` with the reason and the converter does not read the source text.
3. Read the YAML through the separate `IPipelineYamlReader` (the preview reader still discards it). Classic pipelines have no YAML.
4. Build the **plan**: the only secrets/variables a workflow may use, with valid GitHub names (`db-admin-password` → `db_admin_password`,
   `orion-acr` (docker registry) → `ORION_ACR_USERNAME`/`ORION_ACR_PASSWORD`, Azure → `AZURE_CLIENT_ID`/`AZURE_TENANT_ID`/`AZURE_SUBSCRIPTION_ID`, …).
   A service connection is mapped only when the YAML names it. Names only, never values.
5. **Redact** (a private key block or a source larger than the cap stops here: a cut-off source would silently drop steps),
   then send: rules mirroring the gates, the **verified action pins**, the plan, the task inventory and the redacted source between
   random-nonce markers, declared as untrusted data.
6. Extract one workflow from the answer, run the gates, and if blockers remain ask again (default one repair, at most three) with
   the blocking findings. A truncated answer is not repaired (the same limit would cut it again).
7. Result: `ReadyForReview` (every required gate ran, none blocks: *a person may now review it*), `Blocked` (with the findings) or
   `NotAttempted` (reasons: `SourceConsentRequired`, `AiConnection*`, `RedactionBlocked`, `SourceTooLarge`, `ClassicPipelineUnsupported`,
   `SourceYamlUnavailable`, `ModelFailure:<code>`). The deterministic disabled draft of the preview remains the fallback.

**Reading the source (ADR-017).** The task inventory follows conditional insertions (`${{ if }}:`, `else`, `each`), counts `template:` in jobs, stages, variables and `extends`, lists step shortcuts
(`publish`, `download`, …) as `shortcut:<name>`, and reports what a template expression computes as `unknown:computed-steps` instead of skipping it; it never returns values. The self-hosted pool `demands`
that are plain names (`docker`, `npm`) become the plan's **runner labels**: written by the product from the pipeline YAML, stored with the plan, named to the model, and given to the real actionlint (through a
config file the product writes, nothing else configured) as the only extra labels it accepts. actionlint compares their spelling exactly; the `SELF_HOSTED_RUNNER` warning still asks a person to accept that the
destination has runners with them.

**Untrusted YAML is bounded before it is parsed (ADR-018).** Every parse of YAML the product does not control goes through `SafeYaml`, which refuses a document nested deeper than 100 levels before building
any node (the parser's recursion overflows the stack at ~24,000 levels, which ends the process). The structure gate answers `YAML_TOO_DEEP`; the source readers report the YAML as unreadable.

**Task accounting.** The workflow's first line must be `# azure-tasks: converted=A@1,B@2 manual=C@3` (the `TaskAccountingGate`). Every Azure
task of the source (the analyser's inventory plus a text scan, because the analyser misses tasks inside `${{ if }}` insertions) must be in
one list; a `manual` task needs `MANUAL PORT REQUIRED` next to its name. This stops a *silent* drop; it cannot tell that a "converted" task
was converted well.

**Action catalog.** A model cannot know commit SHAs. `ActionCatalog` lists 15 pins resolved from upstream on 2026-11-13 (input names read
from each `action.yml`); the prompt hands them over and the policy gate warns (`ACTION_PIN_NOT_VERIFIED`) about any other pin, because a
made-up SHA fails only when the workflow runs. It is a snapshot: it does not follow releases or advisories and must be refreshed.

**Analysers.** `WorkflowGates:ActionlintPath` / `WorkflowGates:ZizmorPath`, else `PATH`; the API image ships both, pinned and
checksum-verified (`docker/backend.Dockerfile`, amd64 and arm64). `GET /api/workloads/pipelines/convert/status` tells the UI when one is
missing (then no proposal can be approvable).

**What is recorded (provenance).** AI connection, provider, host, model (and the model the provider reported), SHA-256 of the exact prompt and of
the plan, policy version, catalog date, redaction counts by kind (never values), tokens, one line per attempt, time. It is kept with the proposal (below).

## Kept proposals and human approval (UI: the same panel, *Kept proposals for this pipeline*)

An answer a model actually produced is **kept** (`workflow_proposals`, `workflow_proposal_events`); nothing sent (no consent, redaction, size) or a failed
request is not. The convert answer says `saved: true` and `proposalId`; *saved means kept for review, never approved or published*.

| State | Meaning |
| --- | --- |
| `Proposed` | every required gate ran and none blocked: a person may decide |
| `Blocked` | kept as evidence **without its text** (a blocked answer may be exactly the credential the gates stopped); findings name the problem, they do not carry the answer |
| `Approved` | a person accepted *this exact text*, named by its SHA-256 |
| `Rejected`, `Revoked` | a person said no, or withdrew an approval; a reason is mandatory |

`GET /api/workloads/pipelines/proposals?sourceConnectionId&projectId&pipelineId` (no text), `GET …/proposals/{id}` (text, evidence, history),
`POST …/{id}/approve | reject | revoke` (`admin`/`superadmin`; approve is rate limited because it runs the analysers).

**Approval** (`{ workflowSha256, acknowledgedWarnings[], note? }`) checks, in this order, against the **stored text**, never a stored verdict:
the proposal is `Proposed` and has a text; the text still hashes to its hash; the approver's `workflowSha256` equals it (they approve what they
read); the approver is not the author when `WorkflowApproval:RequireSecondApprover` is true (the default; compose: `WORKFLOW_APPROVAL_REQUIRE_SECOND_APPROVER`);
then **the gates run again** on the kept text with today's policy and analysers (a missing analyser or a tightened policy stops an old proposal:
`GatesNoLongerPass`); then **every warning of that run is accepted by name** (`WarningsNotAcknowledged` lists the missing codes). Only then the state moves,
atomically with an append-only event holding who, when, the hash, the note, the SHA-256 of the fresh gate report, the policy version and the warnings accepted.
Two simultaneous approvals record one decision.

Errors: 404 unknown; 403 `SecondApproverRequired`; 409 `ProposalNotPending` / `ProposalNotApproved` / `WorkflowHashMismatch` / `ProposalIntegrityFailure`;
422 `GatesNoLongerPass` / `WarningsNotAcknowledged` (with `details`: codes only); 400 `NoteRequired`.

**The database enforces the same**, not only the service: the text, hash, evidence and author of a proposal cannot be updated; a state can only move
`Proposed → Approved | Rejected` and `Approved → Revoked`; an approved row must carry the hash of its own text; events cannot be updated or deleted;
proposals cannot be deleted.

**Approved is not published.** Approval creates no branch or pull request and does not re-enable the original Azure triggers (the workflow stays
`workflow_dispatch` only; the triggers come back only through a derived proposal, see below). `WorkflowProposalService.RequireApprovedAsync(id)` is the one door a later publisher must use: it returns the text only for an
`Approved` proposal whose recorded approval hash is the text's hash.

## Publication plan (read only): `POST /api/workloads/pipelines/proposals/{id}/publication-plan`

Body `{ destinationConnectionId, repository, baseBranch? }` (`admin`/`superadmin`, rate limited). Only an **approved** proposal is planned
(`RequireApprovedAsync`: the stored text must still hash to the approved hash); nothing about the destination is read for any other. The destination
connection must be a verified GitHub destination. The plan **writes nothing**: the destination is read with GET requests only, inside the one repository,
with the destination connection's own credential (the response says `writesPerformed: false`).

What is read: the repository (exists, archived, default branch), the base branch tip, the file at the workflow path *at that commit* (compared with the
approved hash inside the reader; the text never leaves it), the branch the plan would create, and open pull requests from it.

What comes back: where the workflow goes (`.github/workflows/azdo-{pipelineId}-{name}.yml`, the same path as the preview; a new branch
`migrate/azdo-{pipelineId}-{first 8 of the hash}`), the commit message, the pull request title and description, the findings, the steps a person runs
(clone, branch, save, verify the SHA-256, commit, push, `gh pr create`; every value single-quoted), the approved text, and `planSha256`.

| Finding | Severity | Meaning |
| --- | --- | --- |
| `TargetUnreadable` | Blocker | the destination could not be read: nothing was checked |
| `RepositoryNotFound`, `RepositoryArchived`, `BaseBranchNotFound` | Blocker | the repository (or the credential's view of it) or the base is not usable |
| `WorkflowPathOccupied` | Blocker | a *different* file is at the path on the base branch: publishing would overwrite it |
| `WorkflowWillBeReplaced` | Info | the file at the path is exactly the text of the approved conversion this (Triggers) proposal was derived from: the plan replaces it, on a new branch (`mode: Update`) |
| `AlreadyPublished` | Info | exactly this workflow is on the base branch (line endings ignored): nothing to publish, so not `ready` |
| `HeadBranchExists`, `OpenPullRequestExists` | Blocker | the branch is taken, or a pull request from it is open |
| `EarlierPullRequestOpen` | Blocker | a pull request this product opened earlier for the same file (same organization, repository, base branch and path; the parent conversion, an earlier Triggers proposal, another conversion) is still open: the same file on another branch would conflict with it. Merge or close it, then plan again |
| `EarlierPullRequestUnchecked` | Warning | such a pull request could not be read just now: whether it is still open is not known (never assumed closed) |
| `AttemptBlocksPlan` | Blocker | an earlier attempt of this *exact* plan (same hash) keeps it blocked in the ledger: failed after a possible write, never finished, or published with its pull request closed and its branch gone. Added only when nothing else explains it; changes `ready`, not the hash. See *Releasing an attempt* |

**Creating or replacing.** A plan puts the file on a new branch either as a new file (`mode: Create`) or, only for a **derived Triggers proposal**, as the replacement of the approved text of
its parent conversion (`mode: Update`, with `replacesWorkflowSha256`). The file is replaceable only if the parent is still approved (same pipeline) and the file at the planned base commit is exactly
that text and its blob id is the git id of the bytes that were read; anything else at the path is `WorkflowPathOccupied`. An update names the blob it replaces; `planSha256` covers the mode, the blob and
the replaced text, so a file edited after the plan was shown makes the confirmation stale. A plan that creates a file hashes exactly what it always did.

`ready` means nothing stands in the way, never that anything was published. `planSha256` covers the proposal, the repository, the base branch **and its commit**,
the branch, the path, the commit message, the title and the description: a plan made again after the base moved has another hash, which is what a future
executor must be asked to confirm.

**Text from outside is data in the description.** The pipeline name, user names, model name and the model's notes are untrusted: names go in code spans
(backticks and control characters removed, bounded), and the model's notes go in a fence one backtick longer than any run inside them, so none of them can open a
heading, a link or a mention.

**Why the product does not write the pull request yet.** A push of a file under `.github/workflows/` needs more than `contents: write`: GitHub requires the
`workflow` scope (token) or *Workflows: write* (GitHub App), and opening the pull request needs *Pull requests: write*. F-043 attests a credential with
`Contents: write` only, on purpose, so it cannot be reused; a publication credential is a different, repository-scoped attestation that does not exist yet. The
GitHub simulator does not model those two permissions either (`tools/GhSim` knows `contents` only), so a write path tested against it would be tested
against something more permissive than GitHub. See [ADR-013](../ADR/ADR-013-publication-plan-before-publication.md).

## Publishing the pull request (opt-in): `POST /api/workloads/pipelines/proposals/{id}/publish`

**Off by default.** With nothing configured the product cannot write to GitHub, says `PublicationDisabled`, and the plan keeps working. To enable it an operator sets
`WorkflowPublication:Enabled`, `:AppId`, `:InstallationId`, `:PrivateKeySecretReference` (the App's signing key in the secret store, like F-043's) and
`:AllowedRepositories` (`org/repo,org/repo`). In Compose: `WORKFLOW_PUBLICATION_*`. The credential is **a GitHub App installed with exactly Contents, Pull requests and
Workflows write**; it is never the destination connection's PAT (the same secret reference for both is refused: `PublicationCredentialSeparationFailed`) and nothing falls
back to it. `GET …/publication/status` (admin) says whether it is on and where; `POST …/publication/credential-check` (superadmin) mints and checks the credential for one
allow-listed repository and discards it, writing nothing.

Body `{ destinationConnectionId, repository, baseBranch?, planSha256 }`, **superadmin only**, rate limited. Every guard below is checked before the first write, in this order:

1. the operator enabled it and configured it completely;
2. the person sends `planSha256`, the hash of the plan they saw (`PlanConfirmationRequired` otherwise);
3. **the plan is made again now** (approved proposal, intact text, verified destination, valid repository and branch) and must be the same hash (`PublicationPlanChanged`: the base
   moved, or anything else the plan covers) and ready, with nothing in the way (`PublicationPlanNotReady` with the finding codes);
4. the repository is on the operator's allow-list (`PublicationRepositoryNotAllowed`);
5. the attempt is **written to the ledger before any write** (`workflow_publications`). One live attempt per plan: a second, even a simultaneous one, gets `PublicationAlreadyAttempted`;
6. the writer mints a one-repository token with **exactly** contents, pull_requests and workflows write (anything broader or different is refused), re-checks the installation, the
   repository (same id, not archived) and **the base commit with that credential**.

Then it writes, recording the state before and after each write, one step at a time: **create the branch** at the planned commit → **commit the one file** on it → **open the pull
request** → read the pull request (open, from our branch at our commit into our base) and the file at that commit (the approved text) back. The writer only writes a file named
`.github/workflows/azdo-{n}-{name}.yml` on a branch named `migrate/azdo-{n}-{hash8}`, for text that hashes to the approved hash. The base branch is **never written**. The file is either new (it must
not exist at the planned base commit) or the **replacement of the one text the plan names** (it must exist there with the planned blob id and text, or nothing is written:
`PublicationWorkflowPathChanged`); a replacement is one `PUT` with `sha` = that blob on the new branch and is believed only if GitHub answers **200** (a created file answers 201). It **never merges,
deletes or force-pushes, and never repeats a write**.

| Ledger state | Meaning |
| --- | --- |
| `Started` … `PullRequestOpened` | how far it got (the state is recorded before a write is attempted and again after it) |
| `Published` | all of it written and read back: a pull request is open; nothing is merged |
| `Failed` + `writesPossible: false` | refused before anything was written (credential, base moved, a definite refusal of the first write): **the same plan may be tried again** |
| `Failed` + `writesPossible: true` | something may exist at the destination (the branch was created, a 5xx or a lost answer, a read-back that failed): **the product will not retry**; a person looks. The plan then reports `HeadBranchExists`, so it cannot be published over |

The database enforces the order (no skipped step, no way back, `Published`/`Failed` are final), the shape of the path and branch, the unique live attempt, and refuses deletion.

**Not verified online.** Everything above runs against `tools/GhSim`, which models the `pull_requests` and `workflows` permissions and the `workflow` scope but whose refusal for a
workflow file (status code 403 and its wording) is an assumption, and against scripted answers. The Contents API's behaviour for a workflow file, GitHub's exact answers to a lost write,
and App-token rules have not been confirmed against github.com: that needs a sandbox repository and a throw-away App.

## The Azure triggers: `POST /api/workloads/pipelines/proposals/{id}/derive-triggers`

(`admin`/`superadmin`, rate limited.) Only an **approved `Conversion`** can be a parent; the answer is the detail of a **new proposal** of kind `Triggers` (`parentProposalId` = the
parent, its own id and hash, `triggers: { mapped, differences }`). Nothing is approved or published by it, and the parent is untouched. The pipeline's YAML is read on the server with
the source connection's credential and **never sent to a model** (no redaction, no consent involved); the mapping is fixed rules, so the same Azure YAML gives the same text.

What the mapping does to the approved text: it rewrites the `on:` entry and nothing else (every other line is returned byte for byte; the result is parsed again and must have the
same top-level keys and exactly `workflow_dispatch` plus the mapped triggers). It refuses a parent that already has other triggers, no `on:`, or an `on:` that is not plainly
manual-only.

| Azure | GitHub | Notes |
| --- | --- | --- |
| `trigger:` branches, tags, paths | `push:` branches, tags, paths | absent `trigger` = every push (flagged); `none` = no `push` |
| `*` in a pattern | `**` | Azure's `*` crosses `/`; GitHub's does not |
| plain path `src` | `src`, `src/**` | Azure path filters match by prefix |
| include + exclude | include, then `!exclude` entries | GitHub does not allow both lists; exclude only → `*-ignore` |
| `?` in a pattern, other characters | dropped | named as `TRIGGER_PATTERN_UNSUPPORTED` |
| `pr:` | `pull_request:` | Azure Repos Git ignores `pr:` in YAML (branch policies decide): flagged |
| `schedules:` cron | `schedule:` cron | `always:` and branch filters are flagged; cron is UTC on both sides |
| `batch`, `autoCancel`, `drafts` | not translated | named, so a person can add `concurrency` or an `if:` |
| `resources:` triggers | not translated | named |

Every difference is a `Warning` finding of the `triggers` gate with a stable code (`TRIGGER_*`) and **has to be accepted by name** at approval, together with the gate warnings.
`TRIGGER_STARTS_PRIVILEGED_JOBS` is raised when the approved workflow signs in to Azure, asks for an OIDC token or reads secrets.

Policy: the derived text is judged by `WorkflowGatePolicy.WithAutomaticTriggers()` (same rules; `push`, `pull_request` and `schedule` allowed, anything else `TRIGGER_NOT_PERMITTED`,
`pull_request_target` and `workflow_run` still `DANGEROUS_TRIGGER`); the approval event records the policy version, which ends with `+triggers`.

Errors: 404 unknown; 409 `ProposalNotApproved`, `ParentIsNotAConversion`, `NoAutomaticTriggers`; 422 `SourceYamlUnavailable`, `SourceTooLarge`, `SourceYamlUnreadable`,
`WorkflowNotReadable`, `OnSectionNotFound`, `WorkflowHasOtherTriggers`, `TriggerShapeUnsupported`, `DerivedWorkflowInvalid`, `ProviderReadFailed`; 400 `SourceNotFound`,
`UnsupportedSource`, `SourceCredentialMissing`.

Publishing a derived proposal uses the same plan and publisher; the description says the workflow **starts by itself** and lists the mapped triggers and the accepted differences. It
works on a **free path** (a new file) or, when the manual-only conversion was published and merged and the file is still exactly its approved text, as a **replacement** of that file (see above). Merge
the conversion's pull request first: while it is open the path looks free, and the two pull requests would conflict.

**Not verified.** actionlint checks the syntax of the filters (all eight Orion workflows with automatic triggers pass with the real analysers). Whether the same events start the same
runs on GitHub has not been observed, and no golden workflow has run there.

## Reconciling an attempt: `POST /api/workloads/pipelines/proposals/{id}/publications/{publicationId}/reconcile`

Body `{ destinationConnectionId }` (`admin`/`superadmin`, rate limited). Sets one recorded attempt beside what the destination shows now. **A view, not an action**: GET requests only, inside the one
repository, with the destination connection's read-only credential (never the publishing App's); nothing is written at the destination, the ledger does not change, nothing is stored. The connection
must be a verified GitHub destination for the organization the attempt wrote to (`409 DestinationMismatch`); an attempt of another proposal is `404`.

Read: the repository (archived?), the base branch tip now, the attempt's branch, every pull request from it in **any state** plus the number the ledger recorded, and the file at the workflow path on the
branch and on the base, classified (the approved text / the text that was replaced / something else / nothing; the text is never returned). The response has `verdict`, `findings`, the `observed`
facts and the ledger's own fields, and `writesPerformed: false`.

| Verdict | Meaning |
| --- | --- |
| `OpenPullRequest` | open, at the commit the product wrote, with the approved text on its branch |
| `OpenPullRequestChanged` | open, but the branch moved or its file is not the approved text: do not merge it believing it is |
| `Merged` / `MergedBaseDiffers` | merged and the base has the approved text / merged but the base does not (changed after, or the merge did not bring it) |
| `ClosedWithoutMerge` | closed, nothing reached the base; the branch may remain |
| `BranchWithoutPullRequest` | a branch is left and no pull request uses it: the product will not open one or delete it |
| `AlreadyOnBase` / `NothingFound` | the approved text is on the base and nothing remains / nothing of this attempt exists |
| `TargetUnreadable` / `RepositoryNotFound` | nothing can be said (never shown as a good result) |

Findings (`Info`/`Warning`): `BranchMoved`, `BaseMoved`, `PullRequestMissing`, `MultiplePullRequests`, `OtherPullRequests`, `AttemptNotFinished`, `UnexpectedObjects` (the ledger says nothing was written but
something with the attempt's name exists), `ProductWillNotRetry`, `LedgerStillBlocksPlan` (nothing of the attempt is left at the destination, but the ledger still blocks that exact plan: a superadmin can release the attempt, or the base branch can move and make another plan; also
reported for a published attempt whose pull request was closed unmerged with its branch gone), `AttemptReleased` (a person released the attempt after the product checked the destination). The response also has `ledgerReleased` and
`ledgerBlocksPlan`. The screen shows it per attempt ("Check at the destination"). Not verified online: see ADR-016.

## Releasing an attempt: `POST /api/workloads/pipelines/proposals/{id}/publications/{publicationId}/release`

Body `{ destinationConnectionId, note }`. **Superadmin only**, rate limited, the note 3 to 500 characters (control characters removed). One live attempt per plan is the lock, so an attempt that failed after a
possible write, one that never finished and one published whose pull request was closed unmerged all keep that exact plan blocked; a person cleans the destination, and **the product looks again before
it believes it**: GET requests only, with the destination's read-only credential, nothing written. It releases only if no branch, no open pull request and no merged one remains (a published attempt's own pull
request must be *seen* closed and unmerged). The attempt stays on record exactly as it was; the release is a mark with who, when, why and what the destination showed (JSON without any workflow text).

| Answer | Meaning |
| --- | --- |
| `200` | the attempt, now with `releasedAtUtc`, `releasedBy`, `releaseNote` and `blocksItsPlan: false`; the same plan (same hash) can be published again |
| `400 NoteRequired` | no note, or fewer than 3 characters; checked before anything is read |
| `404` | no such attempt for this proposal |
| `409 PublicationLeftoversExist` | something of it is still there; `details`: `BranchExists`, `PullRequestOpen`, `PullRequestMerged`, `RepositoryNotFound`, `PublishedPullRequestNotClosed`; nothing is marked |
| `409 PublicationStillRunning` | not final and moved less than 15 minutes ago: never released while it could be writing |
| `409 PublicationAlreadyReleased` / `PublicationAlreadyFree` | decided already / it failed before any write, so it never blocked anything |
| `409 PublicationLedgerConflict` / `DestinationMismatch` | the attempt changed while it was checked / the connection is for another organization than the one it wrote to |
| `422 DestinationUnreadable` | the destination could not be read: never taken as clean |

An attempt that never reached a final state and has not moved for 15 minutes (the writer gives up after two) is first **abandoned** (`Failed`, `PublicationAbandoned`, writes possible: nothing says it did not
write) and then released, and only if the destination was read clean. The database allows exactly this one transition on a final attempt (the release columns, empty to filled, once, everything else unchanged);
the unique live index ignores a released attempt. Meanwhile the plan says `AttemptBlocksPlan`. The screen offers the form only after "Check at the destination", to a superadmin, and only where the reconciliation says
`LedgerStillBlocksPlan`. See [ADR-019](../ADR/ADR-019-earlier-pull-requests-and-releasing-a-blocked-plan.md).

## What may be sent to a model

`SourceRedactor.Redact(text)`: credentials (AWS keys, GitHub/Slack/npm/NuGet/Google/Stripe tokens, JWTs, `Authorization: Basic/Bearer` values, connection-string
keys, URL credentials, `password: literal`-style values) become `<REDACTED:KIND:n>`; references (`$(Var)`, `${{ … }}`)
are kept (including `$(Var)`, `${{ … }}`, `${NAME}`, and `$NAME` after `=`; a `$` value after `:` in YAML is a literal), as are service-connection names such as `publishFeedCredentials: my-feed`; control characters are removed; the size is capped; a private key block **blocks** sending. A credential found under a key is masked **everywhere** it appears (`password: x` and a later `echo x`), always with the same placeholder. Redaction reports
what it replaced so an operator can audit it; it never claims text is clean.

## Gates on a model's answer

`ModelOutputExtractor` → `WorkflowGatePipeline`. Rule codes are stable (UI, tests and audit use them).

| Gate | Codes (blockers unless noted) |
| --- | --- |
| structure | `EMPTY_WORKFLOW`, `YAML_PARSE_ERROR`, `YAML_TOO_DEEP`, `MULTIPLE_DOCUMENTS`, `NOT_A_WORKFLOW`, `EMPTY_JOBS`, `JOB_WITHOUT_RUNNER`, `JOB_WITHOUT_STEPS`, `STEP_INVALID`, `TOO_MANY_JOBS`, `TOO_MANY_STEPS`, `WORKFLOW_TOO_LARGE`, `SUSPICIOUS_UNICODE` |
| policy | `UNPINNED_ACTION`, `ACTION_OWNER_NOT_ALLOWED`, `DANGEROUS_TRIGGER`, `TRIGGERS_NOT_DISABLED`, `TRIGGER_NOT_PERMITTED` (only under the trigger policy), `PERMISSIONS_UNDECLARED`, `PERMISSIONS_TOO_BROAD`, `PERMISSION_WRITE_BLOCKED`, `REMOTE_SCRIPT_EXECUTION`, `UNTRUSTED_CONTEXT_IN_SCRIPT`, `SECRET_IN_RUN_SCRIPT`, `SECRET_TO_UNLISTED_HOST`, `SECRET_NOT_IN_PLAN`, `VARIABLE_NOT_IN_PLAN`, `LITERAL_SECRET`, `REDACTION_PLACEHOLDER_LEFT`; warnings: `ACTION_PINNED_BY_TAG`, `RISKY_TRIGGER`, `CHECKOUT_PERSISTS_CREDENTIALS`, `SELF_HOSTED_RUNNER`, `UNLISTED_HOST`, `INPUT_IN_RUN_SCRIPT`, `SECRETS_INHERIT`, `PERMISSION_WRITE`; info: `OIDC_TOKEN_REQUESTED` |
| suppressions | `LINTER_SUPPRESSION_COMMENT` |
| actionlint | `ACTIONLINT_<KIND>` (e.g. `ACTIONLINT_EXPRESSION`, `ACTIONLINT_JOB_NEEDS`, `ACTIONLINT_RUNNER_LABEL`) |
| zizmor | `ZIZMOR_<IDENT>`; `High` blocks, `Medium` warns (policy), lower is info |

A report is anchored to the SHA-256 of the exact workflow. `Approvable` = every required gate ran and nothing blocks;
it means "a human may review this", never "equivalent" or "safe to run".

Tools: `GITHUBMIGRATOR_ACTIONLINT` / `GITHUBMIGRATOR_ZIZMOR` (path) or `PATH`. Locally the real-tool tests are skipped when
they are missing; CI sets `GITHUBMIGRATOR_REQUIRE_TOOLS=true`.

## Tests

| Where | What |
| --- | --- |
| `tests/backend/GitHubMigrator.Domain.Tests/WorkflowGates` | gates, hostile corpus, redactor, extractor, fake and real analysers, process runner |
| `…/Ai` | client against a stub handler (URL, auth, retries, errors, timeouts), connection service, **client against LlmSim** (faults, no credential reaches the model, every hostile answer stopped) |
| `tests/integration/AiConnectionsAgainstSimApiTests.cs` | the real API with PostgreSQL, encrypted secrets and LlmSim |
| `…/Ai/GitHubPublicationWriterTests.cs`, `WorkflowPublicationExecutionTests.cs` | the writer against scripted GitHub answers (what it sends, every way a step can fail and whether something may exist) and the executor's guards with an in-memory ledger |\n| `…/Ai/WorkflowPublicationTests.cs` | the plan (naming, findings, hash, injection-proof description, quoting, order of checks) and the destination reader against scripted GitHub answers |\n| `…/Ai/WorkflowProposalTests.cs` | the approval rules with an in-memory store: binding to the hash, four eyes, gates run again, warnings by name, races, release of the approved text |
| `…/Ai/ConversionTests.cs`, `OrionConverterEvaluationTests.cs`, `PipelineYamlReaderTests.cs` | plan, prompt, orchestrator (fake model), the converter on the ten Orion pipelines with the real analysers (golden answers must reach `ReadyForReview`; 40 hostile edits must be blocked with the expected code), the YAML reader |
| `tests/integration/WorkflowPublicationApiTests.cs` | the publisher on the real API, PostgreSQL ledger and GhSim: branch + file + pull request that match the plan and exactly three writes, every guard, a refusal that frees the plan, a write that fails halfway and is never repeated, simultaneous publishes, the database refusing rewrites and skipped steps, disabled by default |\n| `tests/integration/WorkflowPublicationPlanApiTests.cs` | approved workflow → plan against GhSim over real HTTP: GETs only and only that repository, every conflict from what the destination really holds, no read for an unapproved proposal |\n| `tests/integration/WorkflowProposalApprovalApiTests.cs` | kept proposals and approval on the real API and PostgreSQL: the whole decision flow, concurrent approvals, and the database refusing rewrites and skipped states (needs the analysers; CI installs them) |
| `…/Ai/EarlierPullRequestTests.cs`, `AttemptBlocksPlanTests.cs`, `PublicationReleaseTests.cs`, `WorkflowPublicationReconciliationTests.cs` | the plan's view of an earlier open pull request and of a blocking attempt (what it asks, what it never asks, the hash unchanged), the release service (every leftover, stale versus running, abandonment, races, evidence without text) and the reconciler's offer |
| `tests/integration/WorkflowPublicationUpdateApiTests.cs` | replacement, reconciliation, the earlier pull request and the release flow on the real API, PostgreSQL and GhSim (the database constraint test for the release transition is in `WorkflowPublicationApiTests.cs`) |
| `tests/integration/PipelineConversionAgainstSimApiTests.cs` | the endpoint on the real API: end to end with LlmSim, no source secret reaches the model, consent, failures, access |
| `tests/frontend/ai-connections.spec.ts`, `pipeline-conversion.spec.ts`, `proposal-review.spec.ts`, `publication-plan.spec.ts` | the UI against mocked endpoints |
| `…/WorkflowGates/Golden/orion` + `OrionGoldenCorpusTests` | ten Orion pipelines converted by hand: gates (policy and real analysers), plan accounting, task coverage, honesty rules, redaction; see its `README.md` and `GATE_FINDINGS.md`. **Never executed on GitHub** |
| `tests/live/LiveSmoke.Tests/AiModelLiveSmokeTests.cs` | opt-in smoke against a real model endpoint ([live smoke tests](live-smoke-tests.md)) |
| `tests/simulators/LlmSim.Tests` | the simulator's own contract ([docs](llm-test-environment.md)) |

Simulators are not an oracle: anything a decision depends on must be confirmed against the real provider.
