Feature: F-040 non-authorizing GitHub import intent and policy review
  As an operator
  I want a second human to acknowledge exactly what a future import would attempt
  So that no branch is rewritten and no write privilege is granted by a preview

  Scenario: Non-destructive plan derives only from proven reconciliation
    Given a valid F-032 comparison with matching, source-only, target-only and diverged refs
    When the safe Git import plan is constructed
    Then matching refs remain untouched and target-only refs are preserved
    And only source-only refs are create candidates
    And any diverged ref prevents the intent from being created

  Scenario: Fixed-host GitHub policy inspection is advisory
    Given a current F-039 review and a physical F-038 bundle
    When rulesets and default-branch protection are inspected using GET only
    Then the result is bound to their observed SHA-256 evidence
    And 404 or pagination never implies permission to push

  Scenario: Two independent people must acknowledge risk
    Given a pending SHA-bound intent requested by an admin
    When a different superadmin submits the exact risk acknowledgement
    Then PostgreSQL records the second actor and an append-only Acknowledged event
    But executionAuthorized and readyForExecution remain false

  Scenario: A user cannot approve their own request
    Given an unexpired GitHub import intent
    When the requester tries to acknowledge it
    Then the transition is rejected without a completed event

  Scenario: Stale, expired or duplicate approvals fail
    Given the F-039 review or source bundle has changed
    Or the ten minute intent lifetime has ended
    When a superadmin attempts to acknowledge the intent
    Then the request is rejected and no GitHub call is made

  Scenario: PostgreSQL denies forged write authority
    Given any Pending or Acknowledged intent
    When any client attempts to set writeCapabilityVerified, executionAuthorized
      or readyForExecution to true
    Then the database rejects the mutation
