Feature: F-067 immutable-event incremental preparation phase timings
  Operators must separate queue/retry waiting from the elapsed time
  of a source-only worker's final successful attempt.

  Scenario: Valid final-attempt evidence produces precise timing
    Given an incremental preparation was queued and then claimed
    And its final attempt finished successfully within its lease
    When a superadmin inspects source-scoped preparation phases
    Then queue wait is computed from queue-or-retry event to claim
    And worker occupancy is computed from claim to prepared
    And P95 and maximum processing times use only valid evidence
    And no GitHub destination write is performed

  Scenario: A successful retry is never paired with a stale claim
    Given an initial attempt was claimed then scheduled for retry
    And a later attempt was claimed and successfully prepared
    When the read-only phase summary is computed
    Then the origin is the latest attempt's matching RetryScheduled event
    And the matching Claimed and Prepared events have the same attempt
    And the original attempt's claim never changes the result

  Scenario: Missing or inconsistent evidence does not fabricate timings
    Given a prepared row has no matching final-claim event
    And another prepared row has a claimed-to-prepared duration
      beyond the five-minute lease limit
    When the phase summary is read
    Then both runs count as missingOrInvalidSamples
    And neither contributes to the average, P95 or maximum duration
    And timingEvidenceIncomplete is true

  Scenario: Empty source has no fabricated latency
    Given the source has no preparation records
    When the phase summary is queried
    Then all counts equal zero and all phase durations are null

  Scenario: Default-off privacy and superadmin authorization
    Given incremental queue health is disabled
    When an authorized superadmin queries the phase endpoint
    Then it returns 404 without job metadata
    And anonymous, non-superadmin and unchanged-password callers are denied
    And no source IDs, lease tokens or raw errors are disclosed

  Scenario: Diagnostics cannot change the queue
    Given a real PostgreSQL F-026 event chain
    When phase metrics are computed
    Then no queue state, event history or lease is altered
    And no retry, GitHub remote write, or migration completion is triggered
