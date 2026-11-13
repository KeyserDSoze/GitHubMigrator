# Orion golden corpus

Ten Azure Pipelines of the Orion estate (served by `tools/AdoSim`, source under `tools/AdoSim/seed/files/`) converted **by
hand** to GitHub Actions, one workflow (`<id>-<slug>.yml`) and one sidecar (`<id>-<slug>.expected.json`) each. The
classic pipeline 2006 has no YAML and is not converted.

What it is for: a fixed, reviewed reference against which the gates and, later, an AI converter are measured
(`OrionCorpus.Load()`, `OrionGoldenCorpusTests`). What it is **not**: proof that the workflows work. They have been linted
by actionlint and zizmor and passed the policy gates; **none was ever executed on GitHub**, and the Azure behaviour was
read, not run.

## Equivalence levels (in the sidecar)

| level | meaning |
| --- | --- |
| `full` | every source step has a converted step and no behavioural difference is known; infrastructure assumptions (a runner, a tool) are listed in `notes` |
| `partial` | converted, but at least one known difference or a declared `unsupported` entry; read `notes` before relying on it |
| `manual` | at least one `unsupported` entry blocks a faithful conversion |

`full` needs an empty `unsupported`; `manual` needs a non-empty one (enforced by the tests). Only 2007, 3003 and 4001 are `full`.

## Conventions

- The first line of every workflow is the accounting header `# azure-tasks: converted=… manual=…` (generated from the sidecar); a task in `manual` carries `MANUAL PORT REQUIRED` next to its name.
- Delivered **manual-only** (`workflow_dispatch`); the Azure triggers are in `originalTriggers` and are enabled by a separate approval.
- Azure parameters become typed `workflow_dispatch` inputs, always passed through `env`, never interpolated into a script.
- Every action is pinned to a full commit SHA, every container image to a digest (`ACTION_PINS.md`).
- Group/pipeline variables map 1:1 (secret flag decides `secrets.` or `vars.`); non-secret group variables are exported as env as Azure does;
  names that are not valid GitHub secret names are not invented. Azure service connections become OIDC identities where they were Azure ones.
- Anything not converted faithfully is an `unsupported` entry **and** leaves a visible marker (a comment naming it) in the workflow.
- `acceptedWarnings` lists warnings a reviewer must consciously accept, with the reason; nothing is silenced in the file.

See `GATE_FINDINGS.md` for where the gates and the product's own analyser disagreed with this work.
