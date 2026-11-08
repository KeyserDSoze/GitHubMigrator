Feature: F-050 durable future remote-write coordination with quarantine
  As a migration engine superadmin
  I need one globally fenced, two-person-reviewed destination preparation
  Without enabling a GitHub push, token mint or transport

  Scenario: Competing API replicas cannot hold separate remote write fences
    Given a fresh F-044 packet and an F-047 two-person reviewed blocked attempt
    And the exact GitHub repository ID and owner are server allowlisted
    When multiple replicas propose the same repository concurrently
    Then exactly one proposal and Proposed event are saved
    And the other replicas fail closed

  Scenario: Two distinct superadmins must review
    Given a Proposed fence for a GitHub test repository
    When the original proposer tries to review it
    Then the operation fails
    When a different superadmin rechecks fresh F-044 evidence
    Then the fence changes to ReviewedWriteDisabled
    And all remote write flags remain false

  Scenario: An expired reviewed fence is quarantined
    Given a ReviewedWriteDisabled fence
    When its deadline expires or the worker process crashes
    Then the next poll or mutating request marks it RecoveryRequired
    And no automatic reacquisition or remote push is possible
    And a different superadmin can clear only the F-050 no-write quarantine
    With fresh evidence and an explicit no-remote-transport attestation

  Scenario: PostgreSQL cannot authorize GitHub mutation
    Given any F-050 fence
    When a direct SQL update attempts to enable execution_authorized
    Then the database rejects the update
    And neither a GitHub API mutation nor git push is issued
