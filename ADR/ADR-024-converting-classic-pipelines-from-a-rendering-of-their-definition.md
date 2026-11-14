# ADR-024: converting classic (designer) pipelines from a rendering of their definition

Status: Accepted (0.12.0). Extends ADR-011 (redaction, consent), ADR-017 (reading pipelines completely) and ADR-023 (templates). Until now a pipeline that was not YAML was refused (`ClassicPipelineUnsupported`)
and its work was a manual rewrite.

Context: a classic (designer) pipeline has no file in a repository: its steps live in a build definition, as JSON. The estates this product is for have many of them (the Orion estate has one of eleven, and an
older real estate can have most). What can be built on is limited by what is known:
- Microsoft's Build REST reference documents `BuildDefinition.process` as only `type`; phases, steps and a step's `task` are **not documented**. The Task Group reference documents a task reference as
  `{definitionType, id, versionSpec}`, the shape the simulator emits, so that is consistent by analogy and nothing more. How a real service answers is unconfirmed.
- There is no documented export of a definition to YAML (the reference lists Builds, Definitions and Definition Templates; a community request for it was closed; third-party tools rebuild the YAML from the
  definition and the task catalogue).
- A step names its task by GUID and a version spec (`2.*`), not by the `Name@2` the rest of this product, and a YAML pipeline, uses. The only way to learn what a GUID is, other than a table, is the organization's
  task catalogue (`_apis/distributedtask/tasks`), which is internal: undocumented and marked internal-use in Microsoft's own client library.

Decision:
- **The source text of a classic pipeline is a rendering the product makes of its definition**: read with GET only, rendered deterministically by a program (no model) into Azure-Pipelines-like YAML, returned by the
  same reader as a YAML file would be (`IPipelineYamlReader`) with `Origin = classic-definition` and notes. Everything downstream (redaction, the task inventory, task accounting, the gates, approval, the publication plan and the
  publisher) keeps working on text it already knows. The text starts with comment lines that say it is a rendering, not a file, and list what it could not carry over.
- **What is rendered:** the run name (build number format), the pool (a hosted image only for a hosted pool, otherwise the queue's name, with demands), timeouts, variables (names; values only of non-secret ones;
  groups by name), a `checkout` step when "Get sources" has an option worth carrying (clean, submodules, shallow depth, LFS, or off), phases as `jobs:` (with `dependsOn` and conditions) or flat `steps:` for one plain phase,
  and each step as `task: Name@N` with its display name, `enabled`, condition, `continueOnError`, `alwaysRun`, timeouts, retries, environment and inputs.
- **Naming the tasks.** A table maps (task id, major version) to the name a YAML pipeline writes. It is **generated** from the public `task.json` files (`script/generate_classic_task_catalog.py`, 222 rows from
  microsoft/azure-pipelines-tasks at a recorded commit): the id of a task never changes between its versions, but one id can carry several names by major version (`NuGetInstaller@0`, `NuGetRestore@1`, `NuGetCommand@2`).
  A version the table has no folder for keeps the task's latest known name with the major the definition asked for, and is noted. An id the table does not know is asked of the organization's catalogue (at most 20, one
  read each, best effort); failing that it is **shown by its id** and the model is told to mark it manual. A task group (`metaTask`) is not expanded: shown by id and noted.
- **What it does not carry over, in notes and never silently:** triggers (continuous integration, pull request, schedules, build completion), build options, multi-configuration and multi-agent execution options,
  phase targets that are not agent jobs, the kind of repository when it is not Azure Repos Git, and anything left out for being blank or not a plain name. **Triggers are described, never rendered as `trigger:`**: what
  Azure means by a classic filter (no branch filter at all, settings taken from the definition) is not verified, so deriving `on:` from it would be a guess that starts a workflow by itself. Hence the derivation of
  triggers is **refused for a classic conversion** (`ClassicTriggersNotSupported`, 409, before anything is read) and the workflow stays `workflow_dispatch`.
- **Secrets.** An input or environment value whose *name* looks like a credential (`password`, `secret`, `token`, `apikey`, `privatekey`, `principalkey`, `kubeconfig`) is not shown, unless the value is only a
  variable reference; secret variables are listed by name with no value; an input that is exactly a service connection's id is shown as its name (so the plan can map the connection). The whole text is then redacted like any
  source (ADR-011), and the notes are redacted before they are kept or shown. Consent to send source is still required first.
- **Bounded and never cut:** 20 phases, 300 steps per phase, 80 inputs per step, 500 variables, 50,000 characters per value, 200,000 in all. More than that is refused as `SourceTooLarge`, not truncated.
- **The model is told what it reads** (a paragraph in the question): not a YAML file, bare ids are manual, `enabled: false` does not run, withheld values are not to be invented, triggers are not converted.
- **On record:** the proposal's provenance keeps `SourceOrigin` and `SourceNotes`, the screen shows them, and the pull request description says the source was a classic definition rendered by this product (only for these
  proposals, so no existing plan's text or hash changes). The "include templates" option is ignored for a classic pipeline: an input that happens to be called `template` is not a template.

Consequences:
- Classic **build** definitions can be converted. Classic **release** pipelines (release definitions, another service) are not covered at all.
- The conversion has two readings, one by a deterministic program and one by a model, so two chances to lose or distort something; the notes, the comment header and the approval exist for that. It is still never
  claimed equivalent to the Azure pipeline.
- **Not verified on a real Azure DevOps:** the shape of phases, steps and tasks; that a task reference is `{id, versionSpec, definitionType}`; the catalogue endpoint and its answer; that the service blanks secret inputs
  (the simulator does, and the product does not rely on it); how a classic filter behaves. The live smoke test `Classic_pipeline_shape_is_recorded_as_key_names_only` records them (GET only, key names and value kinds
  only, never a value). Until it is run, "works on classic pipelines" is a statement about the simulator.
- The table can go stale (a task added after its commit, an extension's task): that is what the id fallback and the catalogue lookup are for, and the script regenerates it.

Alternatives considered: sending the definition's JSON to the model (rejected: large, in a shape the inventory cannot count, task ids mean nothing to a model and a secret in an unstructured value is harder to redact);
rendering the triggers as `trigger:` and deriving them (rejected until the semantics are verified); relying on the task catalogue for every task (rejected: undocumented and per-id requests); expanding task groups now
(deferred: the Task Groups API is documented, so it is a reasonable next step); inventing an "export to YAML" (none is documented).
