Feature: F-055 durable possible-transmission guard without a GitHub sender
  As an administrator of source-control migrations
  I want uncertain outbound transfers to remain quarantined after crashes
  Without treating a reviewed intent as permission to push

  Scenario: Only one replica can prepare a possible send
    Given a single reviewed F-052 test-only grant and global F-050 fence
    When six PostgreSQL replicas concurrently prepare the same grant
    Then one prepared hazard and audit entry are committed
    And all other replicas receive an epoch-consumed error

  Scenario: A pre-send marker survives lost acknowledgement
    Given a prepared write-disabled hazard with its one-time token
    When the original preparer commits MayHaveTransmitted
    Then another process can read the irreversible possible-send state
    And no GitHub transport is invoked
    And an automatic retry or abort is refused

  Scenario: Mark and abort race
    Given a prepared write-disabled hazard
    When two replicas concurrently mark and abort the same hazard
    Then at most one terminal state wins
    And no second event can change that state

  Scenario: Stale preparation quarantines instead of resending
    Given a prepared hazard whose F-052 deadline has passed
    When a replica attempts the possible-send mark
    Then the prepared hazard enters OperatorReviewRequired
    And no other automatic attempt can be prepared

  Scenario: Direct SQL must not create authority
    Given a persisted hazard row
    When an operator sets transport_authorized or may_automatically_retry to true
    Then PostgreSQL rejects the update
    And immutable hashes and audit events cannot be changed
