Feature: F-033 interactive review of immutable Git ref evidence
  As a migration administrator
  I need to view saved source-target ref differences and their history
  So that I can review a future migration without initiating one

  Scenario: Open current operation plan directly from discovery
    Given a prepared incremental snapshot and a saved F-029 operation manifest
    When the administrator chooses the ref reconciliation workbench
    Then the exact preparation and plan IDs are passed into the workbench
    And only the backend supplies the proof SHA-256 values

  Scenario: Collect GitHub ref evidence and compare
    Given a current F-029 plan and source-to-target repository mapping
    When the administrator collects live GitHub ref evidence
    Then the F-031 read-only API stores a target observation
    When the administrator compares the evidence with the certified source snapshot
    Then the F-032 PostgreSQL-only API stores an immutable comparison
    And the UI shows counts and decisions without enabling Git writes

  Scenario: Historical comparison remains viewable
    Given a previous F-032 reconciliation is no longer current
    When the administrator selects it from the history
    Then its SHA-256 and source/target ref decisions can be viewed
    And expired evidence cannot trigger a new comparison

  Scenario: Bounded diff pages
    Given a saved reconciliation with more than 50 ref decisions
    When the administrator requests additional results
    Then the nextAfter cursor is sent to the F-032 paginated API
    And earlier rows remain visible without re-running reconciliation

  Scenario: Read failures are not missing data
    Given persisted data fails the server's integrity check
    When an administrator views the comparison
    Then an explicit integrity error is displayed
    And no empty or successful comparison is fabricated

  Scenario: Normal users cannot review or collect ref evidence
    Given a platform user without the administrator role
    When that user opens the reconciliation tab
    Then only access denied is displayed
    And no target ref or reconciliation API is called
