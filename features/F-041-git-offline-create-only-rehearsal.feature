Feature: F-041 offline Git create-only transport rehearsal
  As an administrator
  I need to prove that certified Git source objects can be transferred
  So that future GitHub import can be tested without touching GitHub

  Scenario: Transfer real history and only missing refs into a disposable bare target
    Given an acknowledged, fresh two-person F-040 intent
    And a valid F-035 bundle whose bytes match F-038 PostgreSQL SHA-256
    When a real Git local rehearsal executes
    Then Git clone imports every expected source object and branch/tag ref
    And only CreateCandidate refs are atomically created in a local bare target
    And all target object IDs match their exact source IDs
    And existing matching refs remain unchanged
    And target-only refs are explicitly marked as not materialized

  Scenario: Never operate on a GitHub URL or token
    Given GitImportRehearsal:Enabled is true and a private scratch directory
    When the Git process is launched
    Then network protocols, global hooks, credential helpers and remote URLs are disabled
    And no request can provide a destination path
    And no provider write credentials enter the Git process

  Scenario: Corrupt bundle or forged intent fails closed
    Given a previously sealed F-035 source bundle
    When its bytes or the F-040 safe ref plan SHA are modified
    Then no new destination is kept
    And PostgreSQL does not record a successful rehearsal receipt

  Scenario: Non-destructive SQL barrier remains permanent
    Given a successful local Git rehearsal receipt
    When any actor attempts to set executionAuthorized,
      readyForExecution or destinationWritesPerformed true
    Then PostgreSQL rejects the mutation

  Scenario: No rehearsal without independent intent review
    Given an F-040 intent in Pending or expired state
    When an administrator requests Git rehearsal
    Then the service rejects the run before any Git command
