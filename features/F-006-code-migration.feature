Feature: Auditable code migration
  Scenario: Git mirror
    Given a Git repository with multiple branches and tags
    When an approved migration executes
    Then all selected refs are pushed to the target
    And target ref SHAs are compared with source ref SHAs

  Scenario: TFVC best-effort history
    Given a TFVC branch with changesets and merges
    When FullHistoryBestEffort is selected
    Then changeset IDs and conversion exceptions are recorded
    And only verified reconstructed Git refs are reported as migrated

  Scenario: Idempotent restart
    Given an interrupted migration job
    When the worker resumes from a checkpoint
    Then completed verified stages are not duplicated
    And the operator receives a final migration report
