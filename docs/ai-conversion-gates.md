# AI conversion: model connections and workflow gates

Status: **model connections, gates, a converter that returns a *proposal*, the kept proposals, the human approval and a read-only *publication plan* are
implemented. Writing the branch and the pull request to GitHub is not: a person does it with the plan.** The conversion itself never approves or publishes anything. See
[ADR-011](../ADR/ADR-011-ai-conversion-gates-and-model-connections.md) and [ADR-012](../ADR/ADR-012-kept-proposals-and-human-approval.md).

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
`workflow_dispatch` only). `WorkflowProposalService.RequireApprovedAsync(id)` is the one door a later publisher must use: it returns the text only for an
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
| `AlreadyPublished` | Info | exactly this workflow is on the base branch (line endings ignored): nothing to publish, so not `ready` |
| `HeadBranchExists`, `OpenPullRequestExists` | Blocker | the branch is taken, or a pull request from it is open |

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

## What may be sent to a model

`SourceRedactor.Redact(text)`: credentials (AWS keys, GitHub/Slack/npm/NuGet/Google/Stripe tokens, JWTs, `Authorization: Basic/Bearer` values, connection-string
keys, URL credentials, `password: literal`-style values) become `<REDACTED:KIND:n>`; references (`$(Var)`, `${{ … }}`)
are kept (including `$(Var)`, `${{ … }}`, `${NAME}`, and `$NAME` after `=`; a `$` value after `:` in YAML is a literal), as are service-connection names such as `publishFeedCredentials: my-feed`; control characters are removed; the size is capped; a private key block **blocks** sending. A credential found under a key is masked **everywhere** it appears (`password: x` and a later `echo x`), always with the same placeholder. Redaction reports
what it replaced so an operator can audit it; it never claims text is clean.

## Gates on a model's answer

`ModelOutputExtractor` → `WorkflowGatePipeline`. Rule codes are stable (UI, tests and audit use them).

| Gate | Codes (blockers unless noted) |
| --- | --- |
| structure | `EMPTY_WORKFLOW`, `YAML_PARSE_ERROR`, `MULTIPLE_DOCUMENTS`, `NOT_A_WORKFLOW`, `EMPTY_JOBS`, `JOB_WITHOUT_RUNNER`, `JOB_WITHOUT_STEPS`, `STEP_INVALID`, `TOO_MANY_JOBS`, `TOO_MANY_STEPS`, `WORKFLOW_TOO_LARGE`, `SUSPICIOUS_UNICODE` |
| policy | `UNPINNED_ACTION`, `ACTION_OWNER_NOT_ALLOWED`, `DANGEROUS_TRIGGER`, `TRIGGERS_NOT_DISABLED`, `PERMISSIONS_UNDECLARED`, `PERMISSIONS_TOO_BROAD`, `PERMISSION_WRITE_BLOCKED`, `REMOTE_SCRIPT_EXECUTION`, `UNTRUSTED_CONTEXT_IN_SCRIPT`, `SECRET_IN_RUN_SCRIPT`, `SECRET_TO_UNLISTED_HOST`, `SECRET_NOT_IN_PLAN`, `VARIABLE_NOT_IN_PLAN`, `LITERAL_SECRET`, `REDACTION_PLACEHOLDER_LEFT`; warnings: `ACTION_PINNED_BY_TAG`, `RISKY_TRIGGER`, `CHECKOUT_PERSISTS_CREDENTIALS`, `SELF_HOSTED_RUNNER`, `UNLISTED_HOST`, `INPUT_IN_RUN_SCRIPT`, `SECRETS_INHERIT`, `PERMISSION_WRITE`; info: `OIDC_TOKEN_REQUESTED` |
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
| `…/Ai/WorkflowPublicationTests.cs` | the plan (naming, findings, hash, injection-proof description, quoting, order of checks) and the destination reader against scripted GitHub answers |\n| `…/Ai/WorkflowProposalTests.cs` | the approval rules with an in-memory store: binding to the hash, four eyes, gates run again, warnings by name, races, release of the approved text |
| `…/Ai/ConversionTests.cs`, `OrionConverterEvaluationTests.cs`, `PipelineYamlReaderTests.cs` | plan, prompt, orchestrator (fake model), the converter on the ten Orion pipelines with the real analysers (golden answers must reach `ReadyForReview`; 40 hostile edits must be blocked with the expected code), the YAML reader |
| `tests/integration/WorkflowPublicationPlanApiTests.cs` | approved workflow → plan against GhSim over real HTTP: GETs only and only that repository, every conflict from what the destination really holds, no read for an unapproved proposal |\n| `tests/integration/WorkflowProposalApprovalApiTests.cs` | kept proposals and approval on the real API and PostgreSQL: the whole decision flow, concurrent approvals, and the database refusing rewrites and skipped states (needs the analysers; CI installs them) |
| `tests/integration/PipelineConversionAgainstSimApiTests.cs` | the endpoint on the real API: end to end with LlmSim, no source secret reaches the model, consent, failures, access |
| `tests/frontend/ai-connections.spec.ts`, `pipeline-conversion.spec.ts`, `proposal-review.spec.ts`, `publication-plan.spec.ts` | the UI against mocked endpoints |
| `…/WorkflowGates/Golden/orion` + `OrionGoldenCorpusTests` | ten Orion pipelines converted by hand: gates (policy and real analysers), plan accounting, task coverage, honesty rules, redaction; see its `README.md` and `GATE_FINDINGS.md`. **Never executed on GitHub** |
| `tests/live/LiveSmoke.Tests/AiModelLiveSmokeTests.cs` | opt-in smoke against a real model endpoint ([live smoke tests](live-smoke-tests.md)) |
| `tests/simulators/LlmSim.Tests` | the simulator's own contract ([docs](llm-test-environment.md)) |

Simulators are not an oracle: anything a decision depends on must be confirmed against the real provider.
