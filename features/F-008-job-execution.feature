Feature: Durable and verifiable migration execution
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
