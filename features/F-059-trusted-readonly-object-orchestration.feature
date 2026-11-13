Feature: F-059 trusted source-bound GitHub read-only orchestration
  As a migration auditor
  I want a GitHub proof acquired by our trusted read-only workflow
  Not a success receipt uploaded by a user or manufactured offline

  Scenario: Source snapshot is chosen from durable F-029/F-014 evidence
    Given F-052 F-054 F-055 evidence and a persisted F-029 plan
    When a trusted internal read-only check begins
    Then it selects the source snapshot UUID and refs from PostgreSQL
    And does not accept a client-provided filesystem source path

  Scenario: Fresh provider refs and real Git full-object fetch match
    Given an exact original baseline and create-only candidate set
    And a physical source mirror matching the persisted snapshot
    When the coordinator reads GitHub refs and invokes F-057
    Then F-056 full object verification runs before F-058 journal insertion
    And the journal receives only the runner-generated receipt

  Scenario: The source changes during the Git fetch
    Given the coordinator has read and sealed the original source plan
    When a different snapshot or ref evidence appears after the fetch
    Then the candidate proof is discarded
    And no new F-058 receipt is recorded

  Scenario: Unsafe credentials, unexpected refs and unlinked snapshots
    Given a disabled coordinator, expired token, unexpected remote branch
      or missing persisted F-029 source snapshot
    When an audit is requested
    Then no remote write can happen and unsafe evidence is not stored

  Scenario: An uncertain transfer remains permanently non-retriable
    Given the F-055 hazard state is MayHaveTransmitted
    When an independent local Git object proof matches
    Then the F-058 journal retains only evidence
    And the hazard is neither cleared nor retried automatically
