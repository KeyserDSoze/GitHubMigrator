# ADR-010: Git remote policy, Azure DevOps Server content, and a Services stand-in for tests

Status: Accepted

Context: The git transports hard-coded `https://dev.azure.com` (fetch) and `https://github.com` (push, read-only mirror) with `GIT_ALLOW_PROTOCOL=https`, so Azure DevOps Server content could not be acquired at all and no end-to-end scenario could run without the real services. The Azure DevOps Services adapters also hard-coded `dev.azure.com`/`vssps.dev.azure.com`, so the simulator (Server shaped) could not exercise the Services (Graph) path.

Decision:
- `AzureGitRemotePolicy` decides which remotes the Azure fetch may contact: `https://dev.azure.com` on its default port always; in addition an allow-listed Azure DevOps Server (`ServerEndpointPolicy`) and, in Development only, a Services stand-in origin. The URL is always derived from the immutable source location (`AzureGitSourceLocation`, now carrying the provider and, for Server, the verified connection's server root) and re-checked against the policy before any process starts. `GIT_ALLOW_PROTOCOL` and `protocol.<scheme>.allow` follow the scheme the policy allowed; TLS verification can be skipped for lab servers only, never for `dev.azure.com`.
- The acquisition accepts `azure-devops-server` connections as well (binding in `AzureGitSourceBinding`); the resolver accepts both providers.
- The GitHub git transports derive remote, header scope and protocol from `GitHubEndpoints` (ADR-009).
- `AzureDevOpsServicesEndpoints` (core, Graph, git roots) makes the Services adapters testable against `tools/AdoSim` in its `services` flavour. It is honoured only in Development; elsewhere the API refuses to start with it configured, so production Services is always Microsoft's cloud.
- Simulators are checked against the real services by opt-in, read-only live smoke tests that write a fidelity report (`docs/live-smoke-tests.md`).

Consequences:
- Default behaviour is byte-identical (the existing transport tests still assert the exact git configuration). The guards of the transfer chain (F-036/F-083/F-084) are unchanged; they now run over HTTP in tests: `GitTransportsAgainstSimsTests` moves a repository from ado-sim to gh-sim through the real fetcher, bundle, installation-token push and closure verifier.
- Still missing for a real migration: an executor that chains the stages for N repositories behind the two-person approvals (the F-043/F-083 allow-list is single-repository), the pipeline converter and its publication.
- The Services stand-in and the GitHub simulator encode documentation-based assumptions (e.g. Graph `legacyDescriptor`); the live smoke run is what turns them into facts.
