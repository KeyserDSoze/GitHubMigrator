Feature: F-054 durable read-only GitHub post-send evidence journal
  As a migration operator
  I want immutable reviewed evidence that survives process restarts
  Without granting remote writes or automatic retries

  Scenario: Six replicas persist one receipt
    Given matching SHA-bound F-052 F-053 evidence for one numeric repository
    And a current F-047 blocked attempt and reviewed F-050 and F-052 fences
    When independent PostgreSQL replicas record the same readback concurrently
    Then exactly one immutable evidence record and audit event are committed
    And every replica receives the same immutable record ID

  Scenario: A conflicting observation arrives
    Given a previously recorded complete-ref and object-tip receipt
    When a different remote observation is submitted for the same F-052 grant
    Then the existing receipt remains unchanged
    And the new observation is rejected for independent operator review

  Scenario: Evidence is stale or review barrier changed
    Given an expired grant or altered F-050 F-047 status
    When an observer requests a new journal record
    Then no record is committed and no authority is issued

  Scenario: No GitHub operation can be authorized through database writes
    Given a persisted F-054 readback receipt
    When a direct SQL caller tries to change its state, hashes, or no-write flags
    Then PostgreSQL refuses the modification
    And the append-only audit event cannot be deleted or edited
