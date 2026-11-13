Feature: F-052 remote test intent, independent review and no-retry readback
  As a migration superadmin
  I need a single durable future-write intent for one test GitHub repo
  Without authorizing or performing a GitHub push

  Scenario: The third and fourth actors independently review fresh F051 evidence
    Given the F047 two-person blocked attempt and the F050 reviewed fence
    When a different actor proposes a test-only intent
    And another independent actor repeats the current F051 preflight
    Then the reviewed state is ReviewedWriteDisabled
    And all remote write authorization flags remain false

  Scenario: Competing API replicas cannot replay a reviewed generation
    Given one global numeric GitHub repository ID and one F050 epoch
    When six PostgreSQL replicas propose at the same time
    Then exactly one intention is created
    And cancellation does not make that fence epoch reusable

  Scenario: A stale intention is quarantined and needs manual recovery
    Given a proposal whose F050 deadline has passed
    When a cancellation or worker poll observes expiry
    Then RecoveryRequired and an immutable audit event are persisted
    And no automatic reacquisition or blind push occurs

  Scenario: Complete GitHub ref readback does not imply transport success
    Given the full previous target ref set with annotated tags
    And the exact create-only candidate ref set
    When a fresh provider observation arrives
    Then exact, absent, partial and divergent ref results are distinguished
    And no observation authorizes an automatic retry
