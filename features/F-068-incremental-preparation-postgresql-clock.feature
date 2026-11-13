Feature: F-068 PostgreSQL-authoritative incremental preparation time
  Incremental queue admission, renewal and evidence must be independent
  of the wall clocks on individual worker replicas.

  Scenario: Fast worker clock cannot prematurely claim a retry
    Given a source review-only preparation queued for retry
    And a worker whose clock is twelve hours ahead of PostgreSQL
    When the worker attempts to claim the not-yet-due retry
    Then the database denies the claim
    And no unexpected attempt, retry event or destination write is emitted

  Scenario: Slow worker clock cannot reject a healthy lease
    Given an incremental preparation available on the PostgreSQL clock
    And a worker whose clock is twelve hours behind PostgreSQL
    When the worker claims and prepares with an unexpired database lease
    Then the operation completes with database-clock evidence
    And immutable events remain ordered and valid for F-067 timing

  Scenario: A blocked failure cannot use stale pre-lock time
    Given a worker lease about to expire
    And another PostgreSQL transaction holds the preparation row lock
    When the previous owner attempts to record a failure and must wait
    And the lease expires before the row lock is released
    Then the failure is rejected without modifying lifecycle state
    And the same old token cannot publish a result after successor claim

  Scenario: Database time applies to every lifecycle timestamp
    Given any successfully enqueued, claimed, retried or prepared run
    When its audit events are inspected
    Then the timestamps come from PostgreSQL clock_timestamp
    And F-067 does not need wall-clock agreement across application replicas
    And no migration-complete or GitHub destination writes are authorized
