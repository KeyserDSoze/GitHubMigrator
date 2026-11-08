Feature: Durable, resumable source-only incremental checkpoints
  One reviewed plan advances through committed source snapshots in exact order.
  PostgreSQL checkpoints are not GitHub transfer successes or execution grants.

  Scenario: The immutable reviewed source baseline is checkpointed first
    Given the original F-029 plan and F-025 staging journal are valid
    When a superadmin explicitly confirms the first F-090 checkpoint
    Then only the plan's original source snapshot can be recorded
    And one immutable hashed receipt and audit are stored
    And all GitHub write, retry, dispatch and completion flags remain false

  Scenario: Incremental catch-up never skips a staged source snapshot
    Given two later source snapshots are committed to PostgreSQL
    And the initial plan baseline has an F-090 checkpoint
    When an operator asks to checkpoint the second later snapshot first
    Then the request is rejected as out of order
    When the operator checkpoints the immediately next snapshot
    Then the recorded evidence SHA includes the previous checkpoint SHA
    And only the following snapshot becomes eligible

  Scenario: Six concurrent API replicas converge idempotently
    Given multiple workers receive the same expected snapshot and predecessor
    When they race to record F-090 on PostgreSQL 17
    Then all successful requests return the same original checkpoint and digest
    And exactly one checkpoint and one audit event exist
    And the previous checkpoint chain remains unchanged

  Scenario: Read-only history survives restarts and uses bounded paging
    Given three successive checkpoints are committed
    When a fresh API process reads history with page size two
    Then newest checkpoint is returned first with a next cursor
    And the older page continues without duplication
    And every event, source journal count and predecessor hash is reverified
    And a foreign-plan cursor is rejected

  Scenario: Conflicting changes or SQL tampering never produce proof
    Given a checkpoint exists
    When a caller changes its manifest or predecessor
    Then F-090 refuses the conflicting request
    When SQL attempts to UPDATE its SHA or DELETE its audit
    Then immutable PostgreSQL triggers reject those mutations
    And historic readback remains verifiable

  Scenario: Source discovery remains separate from GitHub sync
    Given the Source sync workbench shows a newer staged source snapshot
    When a superadmin confirms Record next checkpoint
    Then only the existing PostgreSQL source journal is inspected
    And no GitHub API call, Git push, sender or retry occurs
    And no migration is marked complete

  Scenario: Unprivileged actors cannot write or inspect checkpoints
    Given F-090 is disabled by default
    When an anonymous user or ordinary administrator calls its endpoints
    Then access is denied without provider calls
    When a superadmin calls while disabled
    Then the API rejects the request without revealing credentials
