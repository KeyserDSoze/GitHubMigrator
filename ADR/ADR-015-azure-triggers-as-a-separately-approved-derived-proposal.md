# ADR-015: the Azure triggers come back only as a separately approved, deterministic, derived proposal

Status: Accepted (implemented and exercised against the simulators and the real analysers only; the meaning of the mapped triggers on GitHub has not been observed)

Context: a converted workflow is `workflow_dispatch` only (ADR-011/012), so a published conversion never runs by itself and the Azure `trigger:`, `pr:` and `schedules:` are lost.
Giving them back is the step that turns a reviewed file into something that starts on its own, with whatever secrets and environments it uses, on events no person initiates.
That deserves its own decision by a person, not a side effect of approving the conversion.

Decision:
- **A derived proposal of its own.** `Kind = Triggers`, `ParentProposalId` = an **approved `Conversion`**. It has its own id, text, SHA-256, gate report, approval and publication
  plan. Approving the conversion approves nothing about triggers, and approving the derived proposal does not touch the parent (its state, text and approved hash stay as they were).
  Triggers are never derived from triggers. Each request makes a new proposal; earlier ones are kept. The database fixes `kind` and `parent_proposal_id` (immutable), requires a
  parent exactly for `Triggers`, forbids a proposal being its own parent, and restricts deletion of a parent.
- **No model.** The Azure YAML is read on the server (`IPipelineYamlReader`, with the source connection's own credential) and mapped by fixed rules. It is never sent to a model, so it
  needs no redaction and no per-connection consent; deriving makes no model request (asserted in the API tests).
- **The mapping rewrites only the `on:` entry.** Every other line of the approved text is returned byte for byte; the result is parsed again and must be one document with the same
  top-level keys, whose `on` holds `workflow_dispatch` plus exactly the triggers that were mapped. A parent that already has automatic triggers, no `on:`, or an `on:` shape that is not
  plainly manual-only is refused rather than rewritten.
- **What is mapped:** CI `trigger` → `push` (branches, tags, paths; an absent `trigger` means every push, as in Azure, and is flagged), `pr` → `pull_request`, `schedules` → `schedule`
  (cron). Azure's `*` crosses `/`, so it becomes `**`; plain paths match by prefix, so they become `p` and `p/**`; exclude-only filters become `*-ignore`, include plus exclude becomes
  `!` entries (GitHub does not allow both lists); a lone `?` has no equivalent and is dropped. Values go into single-quoted scalars after a character-set check, so a pattern cannot
  inject YAML (tested).
- **Every difference is a warning accepted by name.** The mapper emits stable codes (`TRIGGER_WILDCARD_SEMANTICS`, `TRIGGER_PATH_PREFIX`, `TRIGGER_PR_AZURE_REPOS_IGNORED`,
  `TRIGGER_SCHEDULE_ALWAYS_FALSE`, `TRIGGER_SCHEDULE_BRANCHES`, `TRIGGER_BATCH_NOT_MAPPED`, `TRIGGER_AUTOCANCEL_NOT_MAPPED`, `TRIGGER_DRAFTS_NOT_MAPPED`, `TRIGGER_DEFAULT_CI_ALL_BRANCHES`,
  `TRIGGER_RESOURCE_TRIGGERS_NOT_MAPPED`, `TRIGGER_PATTERN_UNSUPPORTED`, `TRIGGER_STARTS_PRIVILEGED_JOBS`, …). They are stored as `Warning` findings of a `triggers` gate and are added
  to what approval requires, so approving needs each one named, exactly like gate warnings. `TRIGGER_STARTS_PRIVILEGED_JOBS` is raised when the parent signs in to Azure, asks for an OIDC
  token or reads secrets: with these triggers it does so with no person starting it.
- **A policy, not an exemption.** The derived text is judged by `WorkflowGatePolicy.WithAutomaticTriggers()`: the same rules as a conversion, except that `push`, `pull_request` and
  `schedule` are allowed (and `workflow_dispatch`). Any other trigger is `TRIGGER_NOT_PERMITTED`; `pull_request_target` and `workflow_run` stay `DANGEROUS_TRIGGER`. The policy version carries
  `+triggers`, is recorded in the approval event, and is the one used when approval runs the gates again. The proposal service decides which policy judges which kind, in one place.
- **Same publication path.** The plan and the publisher are unchanged; only the title, commit message and pull request description say that the workflow **starts by itself**, list the
  mapped triggers and the differences a person accepted. Four eyes apply as for any proposal.
- **UI.** An approved conversion offers "Propose the Azure triggers"; the derived proposal shows its kind, its mapped triggers, a link back to the parent and each difference as a checkbox.

Consequences:
- **Semantics are not proven.** actionlint accepts the filter syntax of all eight derivable Orion workflows (the other two, a Bicep pipeline and a sandbox, have `trigger: none`), which says
  GitHub's grammar is respected, not that the same events start the same runs. Nothing was run on GitHub, and none of the golden workflows has been. A person accepts the differences by
  name; the product does not claim equivalence.
- **Azure Repos Git ignores `pr:`** (branch policies build pull requests), so `pull_request` adds runs that did not come from the YAML; this is flagged, not hidden. For other Azure
  sources the YAML `pr:` applies, which this ADR does not distinguish.
- **New file, or replacing the parent's file.** Publishing the derived proposal works on a free path, or (ADR-016) as a replacement of the file the parent conversion put there, when that file is
  still exactly the parent's approved text; anything else at the path is `WorkflowPathOccupied`.
- **`batch`, `autoCancel` and `drafts` are not translated** into `concurrency` or job conditions; they are named as differences. Resource triggers (pipelines, repositories,
  containers) have no automatic equivalent and are named too.
- The schema change is idempotent (`ADD COLUMN IF NOT EXISTS` with a `Conversion` default, constraint and trigger added if missing): a 0.5.0 database upgrades in place, existing rows become
  conversions (checked by applying both versions of the DDL to a scratch database twice).
- The restricted-runtime grants script still does not cover the proposal tables (fails closed), as before.

Alternatives considered: letting the model write the triggers (rejected: the one part of the workflow that can be derived exactly would be guessed, and the Azure YAML would have to be sent);
enabling triggers when the conversion is approved (rejected: that approval is about a manual-only file); a switch on the existing proposal (rejected: the approved hash would then name two
different texts); translating `batch`/`autoCancel`/`drafts` into `concurrency` and `if:` (rejected for now: each is a behaviour change a person should choose, and they are named so they can).
