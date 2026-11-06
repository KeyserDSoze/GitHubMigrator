Feature: F-043 one-repository GitHub App write credential attestation
  As a migration superadmin
  I need to verify an independently configured, repository-scoped credential
  So that future GitHub execution does not depend on the read-only PAT

  Scenario: Restrict token issuance to a single allowlisted repository
    Given a fresh unchanged F-042 ref fence and a two-person F-040 approval
    And a separately configured GitHub App private key reference
    When the same F-040 reviewer requests a F-043 credential inspection
    Then a signed GitHub App JWT requests one repository with Contents:write only
    And the response proves the exact installation, repository and limited scope
    And the temporary installation token is discarded after a GET identity check
    And no GitHub repository write is performed

  Scenario: Fail closed on an overbroad or ambiguous token
    Given GitHub returns repository_selection all, excessive permissions,
      a different repository, missing token or a non-201 status
    When the GitHub App inspector evaluates the response
    Then no positive attestation is returned or persisted

  Scenario: No implicit enablement or cross-repository token
    Given GitHubAppWriteCredential:Enabled is false
    When a superadmin requests a token review
    Then the request fails before reading any private key or invoking GitHub

  Scenario: Enforce the same distinct reviewer and proof SHA
    Given a F-042 fence for the exact approved repository
    When the original requester, a different actor or a forged SHA requests a review
    Then no GitHub App token is minted

  Scenario: PostgreSQL cannot turn scope evidence into an execution grant
    Given an immutable F-043 attestation
    When a client sets writeCapabilityVerified, executionAuthorized,
      remoteWritesPerformed or readyForExecution true
    Then PostgreSQL rejects the mutation
