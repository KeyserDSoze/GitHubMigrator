# ADR-009: Configurable GitHub endpoints and simulated source/target for end-to-end tests

Status: Accepted

Context: Every GitHub adapter hard-coded `https://api.github.com` (and the git runners `https://github.com`), so no migration scenario could run without the real service, and a GitHub Enterprise Server destination was impossible. The source side already had a deterministic simulator (`tools/AdoSim`, ADR-005 follow-up for Azure DevOps Server).

Decision:
- Introduce `GitHubEndpoints(ApiRoot, WebRoot)`. The default is github.com; the REST readers (`GitHubConnectionValidator`, destination access, target probe, configuration, traceability, ref observation, import policy/repository, candidate policy, remote tip, and the GitHub App credential inspector) take it as an optional constructor argument. No port or interface changes.
- An override comes only from operator configuration (`Providers:GitHub:ApiRoot|WebRoot|AllowedHosts|AllowHttp`), is normalised by `ServerEndpointPolicy` (allow-listed host, no credentials/query, link-local refused), requires both roots, and plain HTTP is honoured only in Development. A user can never supply a GitHub URL.
- Add `tools/GhSim`, a GitHub Enterprise Server-shaped simulator with real bare repositories, git smart HTTP (`upload-pack`, `receive-pack`), GitHub App JWT/installation tokens, rulesets enforced by a `pre-receive` hook, Actions secrets/variables, contents, pull requests and issues, a request log and fault injection. Contract tests need only the `git` CLI; product tests run the real readers and the real API host against it.
- Simulators are a test bed, not an oracle: behaviour a migration decision depends on is confirmed online. The simulator found one real defect already (422 `No commit found for SHA` handled as "unavailable").

Consequences:
- Production behaviour is unchanged unless the operator configures an override; the default path is covered by unit tests of `GitHubEndpoints`.
- The GitHub git transports (create-only push, read-only closure mirror) use the same endpoints; the Azure DevOps fetch and the Services stand-in are covered by [ADR-010](ADR-010-git-remote-policy-and-services-stand-in.md).
- A deployment targets one GitHub endpoint. Mixing github.com and a GHES instance in one deployment would need a per-connection endpoint (a `github-enterprise-server` provider with a base URL, like `azure-devops-server`); not decided here.
- The destination connection still reports provider `github-enterprise-cloud` when pointed at a simulator.
