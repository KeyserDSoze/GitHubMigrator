# ADR-011: Gates, redaction and model connections for AI-assisted pipeline conversion

Status: Accepted (test bed, gates, connections and a converter that returns proposals implemented; kept proposals and approval followed in ADR-012; publication is not)

Context: Converting Azure Pipelines to GitHub Actions with a model means sending pipeline source to a third party and
accepting text from it that will later run with repository credentials. Neither side can be trusted by default: the
source can hold credentials and attacker-controlled text (prompt injection), and the answer can be wrong, malicious or
simply not a workflow. The previous AI code (`FoundryPipelineReviewer`) only reviewed a disabled scaffold and could only
talk to Azure OpenAI with a managed identity. Real model providers cannot be exercised in CI, and simulators are not an
oracle for them.

Decision:
- **Model settings are a UI-managed AI connection**, a dedicated aggregate (`ai_connections`, `/api/ai-connections`,
  secret `ai-connection-{id}` in the existing secret store), not a new `ConnectionDirection`: the restricted runtime
  grants and many `Direction ==` checks of the transfer chain key on `migration_connections`. A connection is created
  only after a real, tiny test completion succeeded (reachability, key, model name and parameter dialect in one go).
  Endpoint, provider and model cannot be edited: change them by creating a new connection, so consent can never follow
  an endpoint change.
- **One provider-neutral client** (`OpenAiCompatibleChatClient`) covers OpenAI, Azure OpenAI (v1 and deployment routes),
  vLLM, Ollama, LiteLLM and LM Studio. Hosts are operator configuration (`Ai:AllowedHosts`, default the Azure OpenAI
  hosts only); plain HTTP is honoured in Development only; redirects are never followed; provider error bodies are never
  copied into exceptions; retries honour `Retry-After` up to a cap and never retry timeouts or plain 500s.
- **Source may leave only after redaction and explicit consent.** `SourceRedactor` replaces credentials with stable
  `<REDACTED:KIND:n>` placeholders, strips control characters, bounds size, and refuses to send when a private key block
  is present. Sending is a per-connection flag (`SendSourceContent`) that only a `superadmin` can set, only on a verified
  connection, recorded with who and when. `ResolveAsync` re-checks verification, host policy, credential and consent at
  every use.
- **Model output is data, never an instruction or an approval.** `ModelOutputExtractor` accepts exactly one workflow
  (prose around it becomes notes shown to a human). `WorkflowGatePipeline` then runs: structure, deterministic policy
  (pinned and allow-listed actions, manual-only triggers, least-privilege permissions, no literal secrets, secrets and
  variables only from the migration plan, no pipe-to-shell, no untrusted context in scripts, secrets only to allowed
  hosts), suppression-comment detection, and the real **actionlint** and **zizmor**. `Approvable` only means a human may
  now review it.
- **Fail closed.** A required analyser that is missing, crashes, times out, exits with an unexpected code or prints
  nothing is `NotRun`, which is not approvable. zizmor runs with `--no-ignores --no-config --persona auditor` so a model
  cannot mute it. CI installs both tools pinned and checksum-verified and sets `GITHUBMIGRATOR_REQUIRE_TOOLS`.
- **Converted workflows are delivered manual-only** (`workflow_dispatch`); the original triggers come back only through
  a separate approval (`TRIGGERS_NOT_DISABLED`).
- **The converter returns a proposal, nothing more.** `PipelineConverter` redacts, asks, extracts, gates and repairs a bounded number of times;
  consent is checked before it reads the source; the result is `ReadyForReview`/`Blocked`/`NotAttempted` with provenance. A task-accounting gate
  (`# azure-tasks:` header) stops silent drops, and a catalog of verified action pins is handed to the model because it cannot know SHAs.
- **A golden corpus anchors the gates.** Ten Orion pipelines were converted by hand and pass the gates with the real
  analysers; doing so exposed four redaction/secret-scan defects (fixed) and four open decisions (`GATE_FINDINGS.md`).
- **Test bed first.** `tools/LlmSim` (OpenAI-compatible, scripted/hostile/faulty answers, full request log) lets the real
  client, the redactor and the gates be tested end to end, including "no credential reached the model".

Consequences:
- Keeping a proposal and the human approval bound to the workflow hash were built afterwards (ADR-012). The pull-request publication remains to be
  built (it needs the multi-repository executor).
- The verified-pin catalog is a dated snapshot and must be refreshed; a pin outside it is only a warning.
- The task-accounting header is a claim by the model, checked for completeness, not for fidelity.
- The existing TODO scaffold (`if: ${{ false }}`) fails actionlint (`if-cond`). It is left unchanged because three tests
  pin it and it never enters approval; as a fallback it is correctly not approvable.
- The `ai_connections` table is not in `script/postgres_runtime_identity_grants.sql`: a deployment that runs the API
  with the restricted runtime login would be denied access (fail closed) until a reviewed grant is added.
- `PostgresConnectionRepository.ListAsync` still does not project `BaseUrl` (a grants-script column would be needed);
  unrelated to this work, found while reading the code.
- What is verified is behaviour against simulators and static tools. Not verified online: the real providers' error
  bodies, `max_tokens` vs `max_completion_tokens` per model and API version, reasoning models' token budgets (the
  verification ping allows 512 tokens), managed-identity token scope, and how actionlint/zizmor behave on real converted
  workflows beyond the Orion corpus.

Alternatives considered: operator-only configuration (rejected: the user wants it managed in the UI like the other
connections); adding an `Ai` value to `ConnectionDirection` (rejected: breaks the restricted-runtime column grants);
a vendor SDK per provider (rejected: the OpenAI-compatible dialect already covers them with one audited client);
trusting the model's own claims of equivalence (rejected: the gates and a human decide, never the model).
