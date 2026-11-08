Feature: Guarded one-shot GitHub create-only sandbox sender
  The sender is an internal, disabled-by-default experiment.
  It must consume F-082 before one Git push, and reconcile uncertain results.

  Scenario: Disabled by default without network or credential access
    Given the F-083 sender is not explicitly enabled and allowlisted
    When an operator attempts an internal one-shot send
    Then no source bundle, GitHub token, provider GET or Git process is accessed
    And there is no HTTP endpoint or hosted worker for GitHub push

  Scenario: Fresh source and GitHub target are required before F-082
    Given F-081 has independent, immutable sandbox confirmation
    And F-044 source candidates and the physical Git bundle match
    When GitHub's typed refs change or rules prohibit a create candidate
    Then the F-082 possible-send marker is NOT consumed
    And no Git push is attempted

  Scenario: Atomically mark F-082 before a single Git push
    Given the allowed numeric GitHub repository ID is reverified with a
      single-repository GitHub App installation token
    And two provider GET ref snapshots, rules and bundle hashes are stable
    When the operator invokes the internal one-shot sender
    Then F-082 commits MayHaveTransmitted before invoking Git
    And Git uses an HTTPS-only atomic push with empty expected candidate refs
    And the token appears in no Git CLI argument, URL, log or database audit

  Scenario: Ambiguous push cannot be repeated
    Given F-082 has committed its irreversible hazard
    When the Git transport times out or loses its response
    Then the outcome is SandboxRemoteOutcomeRequiresReconciliation
    And CanRetry and MigrationComplete are false
    And the second invocation is rejected by the F-082 journal

  Scenario: Zero exit requires independent remote evidence
    Given Git reports the atomic create-only push accepted
    When GitHub's GET ref and tip readback matches the complete expected set
    Then the result requires an additional independent object-closure audit
    And the migration is not marked complete
    When the remote ref set or object proof differs
    Then operator reconciliation is mandatory and retry forbidden

  Scenario: F-083 is sandbox-only and never enables a public writer
    Given F-083 code has been built into the API dependencies
    Then only one explicitly configured repository ID and org/repo are allowed
    And no queue, HTTP route or hosted worker can trigger the new sender
