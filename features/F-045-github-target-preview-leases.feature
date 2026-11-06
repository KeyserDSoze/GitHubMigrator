Feature: F-045 PostgreSQL destination-wide preview lease fencing
  As a superadmin preparing GitHub migrations across replicas
  I want one short-lived reservation per target repository ID
  So that competing preparations are serialized without authorizing any push

  Scenario: Competing workers cannot reserve the same GitHub repository
    Given an unchanged F-042 observation and F-044 sealed create-only packet
    When eight application replicas claim the same numeric destination
    Then exactly one lease is acquired with a monotonic generation
    And every other replica is denied with PreviewLeaseBusy
    And no remote writes or Git transport occur

  Scenario: Expired workers cannot release or renew after recovery
    Given a live F-045 lease that expired without a release
    When another replica attempts to acquire the same repository
    Then the target is quarantined as RecoveryRequired
    And only a distinct human with a fresh F-044 proof can recover
    And the old token and generation can never release or renew the new lease

  Scenario: Database flags prohibit execution even for administrators
    Given an F-045 lease persisted in PostgreSQL
    When any writer sets execution_authorized, remote_writes_performed or ready_for_execution to true
    Then PostgreSQL rejects the update
    And no success event or remote ref modification occurs
