Feature: Durable and verifiable migration execution

  # First implemented slice: immutable source snapshot -> PostgreSQL staging only.
  Scenario: Concurrent administrators request the same staging job
    Given an immutable source discovery snapshot
    When multiple administrators queue its staging normalization
    Then only one job and one queued audit record exist

  Scenario: Multiple workers cannot own the same attempt
    Given a ready PostgreSQL staging job
    When separate workers try to claim it concurrently
    Then a single lease token and attempt owner are chosen
    And a stale worker cannot mark another worker's attempt complete

  Scenario: Recover a crashed worker
    Given an in-progress staging job whose lease expired
    When a new worker claims it
    Then the attempt counter increases and the old lease is fenced
    And the canonical snapshot is synchronized without duplicate asset revisions

  Scenario: Exhaust bounded retries
    Given staging repeatedly fails without corrupting its source snapshot
    When the fifth bounded attempt fails
    Then the job is marked Failed and has a safe error code
    And its audit events show the attempts and retries

  Scenario: Stage-only safety boundary
    Given no F-005 migration plan has been approved
    When a staging job completes
    Then only the intermediate PostgreSQL catalog changes
    And the job never publishes repositories, Actions or Issues to GitHub

  # Future phases begin after governed F-005 plan approval, not in this slice.
  Scenario: Interrupted migration
    Given a migration was interrupted after a verified push stage
    When a worker resumes with its lease and checkpoint
    Then it does not duplicate completed verified work
    And every stage retains correlation IDs and audit evidence

  Scenario: Incomplete destination
    Given the destination has missing refs or mismatched commit SHAs
    When post-migration verification runs
    Then the job is marked incomplete with actionable errors
    And the operator can export a report without leaking secrets
