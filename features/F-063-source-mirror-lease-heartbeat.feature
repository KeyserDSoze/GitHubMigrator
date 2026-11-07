Feature: F-063 fenced lifecycle heartbeats for local Git source mirrors
  A worker verifying a large trusted Git source bundle must retain
  exclusive ownership without reopening any GitHub write permission.

  Scenario: Owner extends its deadline during physical verification
    Given a durable current source mirror lease
    When physical local Git verification lasts longer than a heartbeat tick
    Then only the same worker token and generation can renew its deadline
    And each successful extension is recorded in the immutable audit ledger
    And physical proof can be committed only using the current deadline

  Scenario: A reclaimed or expired worker cannot regain ownership
    Given an old lease that has expired or been superseded
    When its worker attempts a heartbeat
    Then the lifecycle refuses its renewal
    And the new worker's generation remains valid
    And the old worker cannot commit or quarantine that generation

  Scenario: Re-verification remains bound to original lease admission
    Given physical Git verification occurred after admission
    And the current owner renewed before completion
    When the worker submits a current timestamp-sealed proof
    Then the journal verifies the original lease admission proof floor
    And the old receipt deadline cannot be replayed as the latest lease

  Scenario: A stuck worker cannot heartbeat forever
    Given a mirror worker has held a lease for two hours
    When it requests another extension
    Then the journal refuses the extension
    And the wrapper cannot mark the migration complete

  Scenario: Database renewal fails while source import is running
    Given physical source verification is waiting
    When the heartbeat loses database ownership
    Then the wrapper cancels physical work
    And no source mirror verification is committed
    And no GitHub remote push is performed
