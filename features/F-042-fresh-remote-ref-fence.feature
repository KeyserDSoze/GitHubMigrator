Feature: F-042 fresh GitHub remote ref drift fence
  As a migration operator
  I need a fresh comparison of the immutable target baseline and live GitHub refs
  So that a changed repository cannot pass a future pre-push gate

  Scenario: Ref snapshot with matching SHA and annotated tag types
    Given a current F-031 target observation and verified F-041 Git rehearsal
    When the operator requests a fresh GET-only remote fence
    Then every remote branch and tag name, OID and object type matches exactly
    And a SHA-256 sealed RefSetUnchanged audit is persisted
    But writeCapabilityVerified, executionAuthorized and readyForExecution are false

  Scenario: Remote drift blocks future execution
    Given a branch has been created, deleted or moved after F-031
    When the F-042 reader compares the live refs
    Then the result is RemoteDriftBlocked with a mismatch count
    And no GitHub write method is called

  Scenario: A 404 does not mean no refs
    Given the GitHub refs endpoint is hidden, forbidden or rate limited
    When the operator tries to confirm the remote fence
    Then the request fails closed and does not report RefSetUnchanged

  Scenario: Old evidence never authorizes future pushes
    Given a verified F-042 snapshot older than 30 seconds
    When its history is read
    Then isCurrent is false
    And the SQL no-write flags remain false

  Scenario: PostgreSQL enforces the hard safety barrier
    Given a persisted remote ref fence
    When an attacker attempts to set a remote writes or execution flag true
    Then the database rejects the change
