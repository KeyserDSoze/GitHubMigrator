Feature: F-039 read-only GitHub import readiness
  As a migration administrator
  I need evidence that a verified Git bundle maps to a known destination
  So that I can see blockers without accidentally authorizing a Git push

  Scenario: A reported push permission does not authorize execution
    Given a live F-032 reconciliation and an F-038 SHA-verified Git source bundle
    When GitHub GET /repos reports permissions.push true
    Then the review records ReportedAllowed as advisory
    And ContentsWriteScopeNotVerified and BranchRulesNotVerified remain blockers
    And WriteCapabilityVerified and ReadyForExecution are both false

  Scenario: Strict source and target binding
    Given a certified workpack and bundle digest
    When the caller submits a different SHA-256 or target metadata changes
    Then the review is rejected and no GitHub write is issued

  Scenario: Concurrent replay is idempotent
    Given two API replicas observe the same GET-only repository result
    When they persist the same sealed proof
    Then PostgreSQL retains one immutable row and no privilege grant

  Scenario: GitHub metadata is untrusted
    Given the API returns a wrong owner, oversized JSON, 404 or a rate limit
    When the reader classifies the result
    Then it never reports verified write permission or an executable task

  Scenario: PostgreSQL independently denies write flags
    Given a persisted source and destination readiness review
    When a caller attempts to set ready_for_execution or write_capability_verified true
    Then the database rejects the mutation even without API authorization
