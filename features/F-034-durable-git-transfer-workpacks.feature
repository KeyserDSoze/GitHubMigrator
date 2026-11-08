Feature: F-034 durable transfer preparation with a hard write barrier
  As an operator
  I need a resumable, auditable workpack for a current Git ref reconciliation
  So that future Git transfer work can survive crashes without granting writes

  Scenario: Current proof creates one idempotent workpack
    Given a current F-029 manifest and SHA-verified F-032 reconciliation
    When two administrators enqueue the same transfer preparation
    Then exactly one PostgreSQL workpack and one Queued event exist
    And the workpack is never ready for execution

  Scenario: Forged or stale evidence is rejected
    Given a revoked F-029 plan or stale F-032 comparison
    When transfer preparation is requested
    Then no new workpack is committed

  Scenario: Worker crash and lease recovery
    Given a worker has claimed a workpack and checkpointed EvidenceChecked
    When its lease expires due to a crash
    And a second replica reclaims the workpack
    Then its starting phase is EvidenceChecked
    And the previous worker cannot write a new checkpoint
    And only the second worker can reach WriteBlocked

  Scenario: No GitHub writes are permitted
    Given a perfectly matching Git reconciliation
    When transfer preparation reaches the final barrier
    Then the workpack is Blocked with DestinationWritesNotAuthorized
    And SQL rejects ready_for_execution true
    And no provider write call or Git process is invoked

  Scenario: JWT and audit
    Given an unauthenticated or non-admin caller
    When transfer preparation is requested
    Then the API rejects the request without scheduling work
    And administrators can inspect the bounded immutable event history
