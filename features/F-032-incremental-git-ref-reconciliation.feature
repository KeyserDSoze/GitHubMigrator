Feature: F-032 proven incremental source-target Git ref reconciliation
  As a migration operator
  I want to compare an immutable Azure DevOps snapshot with verified GitHub ref evidence
  So that an incremental sync can be reviewed without writing to GitHub

  Scenario: Independent current source and target proofs are required
    Given a current immutable F-029 operation plan for a GitMirror mapping
    And a current F-031 GitHub target ref observation for the same mapping
    And the original F-014 source manifest or complete legacy source snapshot
    When an admin requests reconciliation using both exact evidence digests
    Then no new GitHub or Azure DevOps request is performed
    And immutable PostgreSQL comparison evidence is stored
    And readyForExecution remains false

  Scenario: Ref equality and preservation are conservative
    Given one matching branch, one source-only tag and one target-only branch
    When reconciliation is computed
    Then one ref is recorded as MatchingObjectId
    And one ref is recorded as CreateCandidate with unverified write rights
    And one ref is recorded as TargetOnlyPreserved without deleting the target

  Scenario: Partial source coverage cannot prove deletion or absence
    Given a legacy source snapshot without complete inventory coverage
    When reconciliation is requested
    Then SourceRefCoverageUnverified prevents a persisted comparison

  Scenario: Stale proof cannot produce a current comparison
    Given the target credential has been revalidated or the source scan superseded
    When reconciliation is requested with an old proof
    Then no new comparison is committed

  Scenario: Replayed comparisons are idempotent
    Given the same immutable source snapshot and target observation digest
    When a second replica requests the same comparison
    Then its persisted reconciliation identity and SHA are unchanged

  Scenario: SQL row tampering is detected
    Given a persisted F-032 reconciliation
    When one ref, blocker, count or digest is modified in PostgreSQL
    Then reading the corrupted comparison fails closed

  Scenario: No execution authorization is conferred
    Given a perfectly matching source and target ref inventory
    When reconciliation is complete
    Then the F-029 operation plan remains blocked
    And the execution-authorization endpoint still denies writes
