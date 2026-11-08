Feature: Independent manual confirmation without GitHub sender authority
  An operator confirms an immutable F-080 sandbox intent, but no person
  gains permission to write GitHub refs or to automatically retry.

  Scenario: Distinct humans confirm one live audited handoff
    Given the F-078 session and F-080 intent are current and SHA-sealed
    And the F-055 hazard remains PreparedWriteDisabled
    And the F-052 grant, F-050 fence, F-047 attempt and F-045 lease match
    When the F-080 requester tries to confirm the same intent
    Then confirmation is refused
    When a distinct superadmin confirms the intent
    Then exactly one immutable ConfirmedWriteDisabled receipt is recorded
    And a single Confirmed audit event is recorded
    And no sender, refspec, write authority or retry capability is issued

  Scenario: Six independent connections confirm one SHA
    Given six PostgreSQL connections share the same repo and confirming actor
    When they confirm a current F-080 intent concurrently
    Then all return the same receipt ID and SHA
    And one confirmation and one audit row exist
    When an alternate confirming actor tries to replace it
    Then the request is refused without modifying the original

  Scenario: Possible send blocks even idempotent confirmation
    Given a recorded F-081 confirmation exists
    When its F-055 hazard is marked MayHaveTransmitted
    Then inspection reports HistoricalOnly and no execution rights
    And repeating the confirmation refuses a retry

  Scenario: A prior hazard marker wins the shared repo lock
    Given another connection holds repository advisory lock 733050
    When that connection marks MayHaveTransmitted and commits
    Then a waiting F-081 confirmation refuses the handoff
    And no confirmation audit is written

  Scenario: SQL tampering and cross-scope inspection are forbidden
    Given a stored confirmation and its unique audit event
    When a client updates can_dispatch or the evidence SHA or deletes either row
    Then PostgreSQL triggers reject the operation
    When an operator requests the receipt from another plan or intent
    Then the API returns no receipt

  Scenario: Production disabled and JWT role matrix
    Given F-081 is off by default
    When an anonymous client or a non-superadmin invokes F-081 routes
    Then authorization rejects the request
    When a superadmin invokes them
    Then the API refuses with SandboxDispatchConfirmationDisabled
    And there are no GitHub provider requests
