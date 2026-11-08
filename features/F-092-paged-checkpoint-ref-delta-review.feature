Feature: F-092 bounded, verified source-ref delta inspection
  Read an immutable F-091 checkpoint's F-025 ref changes without
  granting remote GitHub write, retry, or migration completion.

  Scenario: Paged source ref changes are scoped to a sealed checkpoint
    Given a version 2 checkpoint and its independently verified F-091 proof
    When a superadmin reads the first page of ref changes
    Then only canonical repository/ref identifiers and before/after fingerprints appear
    And a bounded keyset cursor selects the next page without duplicates
    And unknown repository mappings are marked unmapped, not silently dropped

  Scenario: Legacy, foreign and invalid requests fail closed
    Given a version 1 F-090 checkpoint with count-only evidence
    When an operator requests its F-092 ref-change details
    Then the API refuses to imply a cryptographic ref attestation
    And foreign-plan checkpoints and foreign-source cursors are rejected
    And user, admin or anonymous actors cannot read the details

  Scenario: Source-only API cannot dispatch GitHub writes
    When an authorized operator browses F-092 pages
    Then no target provider, credential, sender or GitHub push is invoked
    And CanPush, CanRetry, CanDispatch and MigrationComplete are false
