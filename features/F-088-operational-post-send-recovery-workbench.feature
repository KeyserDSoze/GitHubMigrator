Feature: Operational recovery workbench backed by immutable F-082/F-054/F-058/F-085 evidence
  Superadmins review a possible-send epoch and its independent proofs.
  Neither the API nor the UI can authorize a GitHub push or retry.

  Scenario: A crashed sender remains pending on the timeline
    Given F-082 was committed for a reviewed sandbox plan
    And the sender stopped before any post-send readback was recorded
    When a superadmin opens the plan's recovery workbench after restart
    Then the PostgreSQL timeline shows AwaitingIndependentReadback
    And F-082 evidence is visible, F-054 and F-058 evidence is absent
    And every push, retry, dispatch and migration-complete flag is false

  Scenario: Recovery transitions are based on independently persisted evidence
    Given F-086 contains the original typed ref baseline after F-082
    When an independent operator records F-054 exact remote tips
    Then the workbench offers VerifyFullObjectsReadOnly
    When a separate independent operator records a valid F-058 closure
    Then the workbench offers CertifyIndependentClosure
    When an additional independent actor records an F-085 checkpoint
    Then the workbench offers no further action
    And it does not report the migration complete

  Scenario: A remote mismatch or missing historical baseline cannot be retried
    Given an F-082 epoch was recorded
    When F-054 observes unexpected target refs
    Then the workbench requires manual operator review
    And the UI offers no GitHub push or object-verification button
    When the F-086 typed baseline cannot be verified
    Then the workbench refuses to invent historical refs
    And the UI offers manual review instead of a network action

  Scenario: Bounded history never leaks another plan's data
    Given multiple immutable F-082 epochs were persisted
    When a superadmin loads a specific preparation and plan
    Then results are ordered by PostgreSQL time and paginated by a cursor
    And the maximum page size is twenty
    And a cursor from another plan is rejected
    And no token, raw ref name, Git URL or bundle is returned

  Scenario: Corrupt audit evidence fails closed
    Given F-054 contains duplicate immutable events
    When the workbench verifies its history
    Then a conflict error is returned instead of an empty or successful row
    And the UI displays the integrity error

  Scenario: Only a superadmin can inspect or start manual read-only recovery
    Given the workbench is disabled by default
    When an anonymous user, standard user or admin requests it
    Then the API rejects access
    When a superadmin requests it without explicit allowlist configuration
    Then the API refuses without contacting GitHub
    When a superadmin confirms a permitted recovery step
    Then only the existing F-087 GET-only or F-085 checkpoint route runs
    And no F-083 sender, push or retry is triggered
