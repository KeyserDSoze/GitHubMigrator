# What converting Orion by hand showed about the gates

Written while authoring the corpus. "Fixed" means a change to product code with tests in this same change.

## Found and fixed

| finding | where | change |
| --- | --- | --- |
| `publishFeedCredentials: orion-nuget-feed` was masked as a credential, although it is a *service connection name*: the conversion loses exactly what it needs to map to secrets | `SourceRedactor` (`SENSITIVE_LITERAL`) | keys that hold connection names and selector keys (`...Type/Scheme/Method/Mode/Selector`) are not masked; similar keys that hold credentials still are |
| `/d:sonar.token="$SONAR_TOKEN"` was reported `LITERAL_SECRET` in a generated workflow: a shell variable is a reference | `SecretPatterns` | after `=` a pure `$NAME` / `${NAME}` is a reference; after `:` (a YAML value, expanded by nothing) it stays a literal |
| `Password=${DB_PASSWORD}` / `Password=$(Var)` in a connection string and `https://u:$(Token)@host` were masked | `SecretPatterns` | syntactic references (`$(x)`, `${x}`, `$env:x`, `%x%`) are never credentials |
| `Authorization: Basic <base64>` / `Bearer <opaque>` was **not** masked (the scheme word split the value): a real redaction gap | `SecretPatterns` | new `AUTHORIZATION_HEADER` pattern; references after the scheme are kept |
| a credential found under a key was masked only there: `password: x` plus a later `echo x` leaked the key-less copy (found by the end-to-end test against the Azure stub) | `SourceRedactor` | every other occurrence of a value the scanner identified is masked too, with the same placeholder |

## Found and not fixed (decisions for a human)

1. **actionlint rejects custom self-hosted runner labels** (`runs-on: [self-hosted, linux, docker, npm]` → `runner-label`).
   The source (2003) demands agents with `docker` and `npm`. The gates run actionlint without any configuration on purpose, so
   an operator with custom labels cannot pass. Declared in the workflow as `[self-hosted, linux]` and in `notes`. A per-plan
   list of labels written by the product (never by a model) into the isolated actionlint config would fix it.
2. **`ZIZMOR_SECRETS_OUTSIDE_ENV` (Medium) on eight of ten workflows.** True and useful (repository-level secrets, no
   environment), but the source has the same property because Azure variable groups are not environment-scoped. Accepted per
   workflow with the reason; moving secrets into environments adds approvals the source does not have.
3. **Container images must be pinned by digest** (zizmor `unpinned-images`, High). 2005 used `dotnet/sdk:8.0` and `redis:7`;
   they are pinned now, which freezes them (security update lag). Digests are in `ACTION_PINS.md`.
4. **`AzureYamlTaskAnalyzer` does not see tasks inside conditional insertions** (`- ${{ if ... }}:` in a steps list): 3001's
   `AzureCLI@2` is missing from the inventory the product shows. It also counts `template` only in `steps`, not
   `variables:`/`stages:`/`jobs:` templates (2001's `variables.yml`). The task-coverage test scans the source text as well. This
   is the existing preview code and changing its output affects pinned tests, so it is left for a deliberate change.
5. **Approvals, branch checks and environment protections** (Azure environments prod, infra-prod, staging) live outside the YAML; the
   workflows reference GitHub environments of the same names and every sidecar says so. Nothing creates them.
6. **Tool assumptions the gates cannot see**: trivy on the runner (2001), a signing certificate in the store (2005), the
   quality-gate report path (2002), `webapps-deploy` runtime stack (2001). Listed in `notes`.
7. The unaccepted-warning rule counts zizmor Medium as a warning; Low and Informational findings (`concurrency-limits`,
   `anonymous-definition`, `undocumented-permissions`, `use-trusted-publishing`) are information only.

## Verified, and how

- actionlint 1.7.12 and zizmor 1.32.0 (`--offline --no-ignores --no-config --persona auditor`) report no blocker on any workflow.
- Every action input name used was read from the `action.yml` at the pinned commit; every commit SHA and image digest was resolved
  from the upstream repository/registry the day the corpus was written.
- Not verified: behaviour on GitHub, that tags have not moved since, vulnerabilities of the pinned versions.
