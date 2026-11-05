Feature: F-031 live read-only GitHub target ref evidence
  As an authorized migration operator
  I need an auditable, immutable snapshot of target Git branch and tag refs
  So that a later independent reconciliation can be prepared without writes

  Scenario: Current operation plan allows read-only evidence capture
    Given a current F-029 manifest bound to a known source-to-target mapping
    And the destination GitHub credential is verified
    When an admin requests target ref evidence using the exact manifest digest
    Then only fixed-host GET calls read Git branches and tags
    And Git object SHA identities including annotated tags are retained
    And an immutable SHA-bound observation is appended to PostgreSQL
    And readyForExecution is false

  Scenario: Missing or hidden repository does not become an empty inventory
    Given the GitHub refs endpoint returns 404
    When target ref collection is requested
    Then the request fails without persisting a new observation
    And repository absence and target availability remain unverified

  Scenario: Evidence cannot outlive planning and credential validation
    Given GitHub returned ref observations
    When the source snapshot or review is superseded before the DB commit
    Or the destination credential validation timestamp changes
    Then no current ref observation is committed

  Scenario: Stored observations are immutable and verifiable
    Given a persisted GitHub target ref observation
    When a ref object SHA or object type is changed in storage
    Then subsequent reads fail with RefEvidenceIntegrityFailed

  Scenario: Bounded read and privacy
    Given an observation with many branch and tag refs
    When the operator reads the observation with take at most 100
    Then the response contains one sorted page and a continuation name
    And no access token or raw GitHub payload appears

  Scenario: Security and execution boundaries
    Given an unauthenticated user or a non-admin
    When target ref collection is attempted
    Then no GitHub provider request occurs
    And no GitHub writer or approved permit exists
