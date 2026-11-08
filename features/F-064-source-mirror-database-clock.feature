Feature: F-064 authoritative database time for local source-mirror leases
  Cross-replica Git source verification must use one trusted timestamp
  authority to decide lease ownership and proof freshness.

  Scenario: Lease starts on the current PostgreSQL clock
    Given a canonical read-only Git source mirror and a PostgreSQL journal
    When a worker acquires a new lease
    Then the lease admission instant comes from PostgreSQL
    And the normalized deadline is exactly fifteen minutes after admission
    And no GitHub remote write is authorized

  Scenario: Heartbeat uses the database clock without changing admission
    Given an existing unexpired lease
    When its owner successfully renews the lease
    Then the new deadline is derived from the current PostgreSQL time
    And the original F-062 proof freshness floor stays unchanged
    And the new immutable event has a database-clock timestamp

  Scenario: Expiration is checked only after waiting for the lock
    Given a previous worker lease that is approaching expiration
    And a new worker is waiting on the PostgreSQL advisory lock
    When the old lease expires before the new worker acquires that lock
    Then the next worker may safely reclaim a new generation
    And the previous owner cannot renew or complete the new generation

  Scenario: Completion is fenced by the database clock
    Given a valid local Git SHA-verified proof
    When the current owner finishes and saves the proof
    Then PostgreSQL time determines ownership and receipt freshness
    And verified status and audit timestamps use PostgreSQL time
    And migration completion and remote writes remain forbidden
