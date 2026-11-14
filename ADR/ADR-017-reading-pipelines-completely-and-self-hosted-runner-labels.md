# ADR-017: read a pipeline completely, and keep the runner labels its pools demand

Status: Accepted (exercised against the Orion estate, the simulators and the real actionlint/zizmor; nothing was run on GitHub)

Context: converting the Orion estate by hand (`tests/backend/GitHubMigrator.Domain.Tests/WorkflowGates/Golden/orion/GATE_FINDINGS.md`) left two decisions open.
1. `AzureYamlTaskAnalyzer`, the product's inventory of what a pipeline does, did not follow conditional insertions (`- ${{ if … }}:`), counted `template:` only inside `steps:`
   and ignored step shortcuts such as `download:`/`publish:`. The deterministic preview and its checklist therefore listed fewer steps than the pipeline has, and said nothing about it.
   (The task-accounting gate was never affected: it also scans the text.)
2. actionlint knows only GitHub's runner labels. A pool that demands `docker` and `npm` could therefore be converted only as `[self-hosted, linux]`, losing what the source asked for.

Decision, the inventory:
- **Conditional insertions are followed.** `if`, `elseif`, `else` and `each` entries in a steps list are read into their lists, nested to any depth up to a limit, in document order. A list reached
  through an insertion is read once however many aliases lead to it (documents whose aliases refer to one another through insertions cannot make a small file expensive: tested against a 2^23 blow-up).
- **What cannot be read is reported, not skipped.** Steps that a template expression produces (`- ${{ step }}`, `steps: ${{ parameters.steps }}`, an insertion with a scalar under it) are `unknown:computed-steps`;
  computed `jobs`/`stages` likewise; a step with none of the known keys is `unknown:unrecognized-step`. The preview lists each as a step to review, with a blocker, like every other step.
- **More is counted.** `template:` in `jobs`, `stages`, `variables` (also inside insertions) and `extends:` (once each, not twice); the shortcuts `publish`, `download`, `downloadBuild`, `getPackage`,
  `reviewApp` as `shortcut:<name>`. Values (paths, conditions, feed names) are never returned. The Orion sidecars now list the two variable templates they had not counted
  (2001 `pipelines/templates/variables.yml`, 2003 `variables/common.yml`).
- **The text scan stays** in the task-accounting inventory as an independent second reading (it also works when the YAML cannot be parsed or a limit was reached); on the Orion corpus the analyser now
  sees every task the scan sees, and reports nothing as unknown.

Decision, runner labels:
- **The labels come from the source, written by the product.** `AzureRunnerLabels` reads `pool:` mappings with `demands:` from the pipeline YAML: plain capability names only (`docker`, `npm`, `python3`),
  not operators (`Agent.OS -equals Linux`), variables or expressions, not labels GitHub or every runner already has, not from Microsoft-hosted pools (`vmImage`); at most 16, deduplicated, in order of appearance.
- **They travel with the plan.** `ConversionPlan.RunnerLabels` (stored with the proposal; plans stored before have none), part of the plan's hash only when there are some, a note in the plan, one line in
  the prompt ("a job that needs such a pool may use `runs-on: [self-hosted, linux|windows|macOS, <only these labels>]`; no other custom label is accepted; say in the Notes that the destination needs runners with them").
  Approval and the derived Azure triggers re-run the gates with the stored plan, so what was approved is judged by the same labels it was proposed with.
- **actionlint alone is configured, with a file the product writes.** When a plan has labels, `ProcessWorkflowToolRunner` writes `self-hosted-runner: labels:` into the private directory of the run, outside
  `.github/workflows`, and passes it with `-config-file`; nothing else is ever configured (no ignore rules). Every label is validated again there, and a label that is not a plain name means the gate did not run
  (never repaired or quoted around). No label, no config: the run is exactly what it was. zizmor is not told.
- **Measured, not assumed:** actionlint compares the spelling of a label **exactly** (`DOCKER` does not satisfy `docker`); the plan keeps the source's spelling and the prompt hands it to the model in that spelling.
  Without labels the real actionlint reports every custom label; with them it reports none; a label the source did not demand is still reported. The Orion 2003 golden now keeps `[self-hosted, linux, docker, npm]`.

Consequences:
- A label here lets a workflow pass one check; it creates no runner. `SELF_HOSTED_RUNNER` (and zizmor's `self-hosted-runner`) still ask a person to accept that the destination has runners with these labels.
- Demands that are not plain names (`Agent.OS -equals Linux`) are not translated: the OS comes from `linux|windows|macOS` in the `runs-on` array, as a person or the model chooses.
- More steps are listed by the preview, so its checklist is longer than before for pipelines with insertions, variable/stage templates or `download:` steps: that is the point, not a regression.
- Not verified: that GitHub treats label case the way actionlint does (it is not assumed either way).

Alternatives considered: letting the model name labels (rejected: it would be choosing what the gate accepts); labels from pool *names* (rejected: a pool name is Azure's concept and mapping it to a label is a decision for
whoever owns the runners); an operator-configured global list (possible later; the per-plan list needs no new setting); ignoring actionlint's `runner-label` kind through policy (rejected: it would accept any label).
