Feature: F-069 bounded and auditable incremental worker crash recovery
  Worker crashes must not produce an unaudited second attempt or
  let a stale lease mutate PostgreSQL or approve a GitHub write.

  Scenario: Four replicas race to recover one expired lease
    Given a review-only incremental job leased to a crashed worker
    And the lease has expired according to PostgreSQL time
    When four independent PostgreSQL workers attempt to claim it
    Then exactly one worker receives the next attempt and new token
    And LeaseReclaimed for the old attempt precedes Claimed for the new one
    And the old owner cannot prepare, retry or overwrite the job
    And the recovered worker can finish source review-only preparation
    And F-067 reports a valid event-backed final-attempt sample

  Scenario: Ordinary queued retry is never called crash recovery
    Given a failed attempt scheduled RetryScheduled
    When a new worker claims the delayed Queued job
    Then a normal Claimed event is written without LeaseReclaimed

  Scenario: Expired fifth attempt becomes terminal only once
    Given a running incremental preparation at its fifth attempt
    And its database lease has expired
    When multiple worker replicas poll the queue concurrently
    Then exactly one Failed event with LeaseExpiredAfterMaxAttempts is persisted
    And the job cannot receive a sixth attempt or destination-write approval

  Scenario: Expired terminal cleanup is bounded under heavy backlog
    Given 67 expired Running preparations at max attempts
    When the first worker polls for source work
    Then no more than 64 are terminalized in the first transaction
    And the next poll terminalizes the remaining three
    And repeat polls do not duplicate terminal events
    And no worker is blocked by another worker's locked row
