Feature: Safe source and destination connections
  As a migration administrator
  I need to register and verify source and destination credentials
  So that discovery can use a reusable secure connection

  Background:
    Given I have signed in as a platform administrator
    And I have changed my temporary password

  Scenario: Test an Azure DevOps Services PAT
    Given a valid PAT for an Azure DevOps organization
    When I test a source connection
    Then a read-only projects endpoint is called
    And validation confirms the organization can be read
    And the PAT is not written to the database

  Scenario: Save a GitHub Enterprise Cloud destination
    Given a valid GitHub PAT and existing target organization
    When I save a destination connection
    Then the token is verified against my GitHub identity
    And the target organization existence is confirmed
    But organization write access is explicitly marked unverified
    And the API returns no PAT

  Scenario: Invalid credentials
    Given the remote provider rejects my PAT
    When I try to save a connection
    Then no connection or secret is persisted
    And I see an actionable error without disclosing the token

  Scenario: Validate an existing connection again
    Given I have saved a source connection
    When I click "Verify"
    Then the stored secret is loaded privately
    And the connection status is updated without exposing it

  Scenario: Rotate a PAT
    Given an existing working connection
    When I submit an invalid replacement PAT
    Then the original working secret remains unchanged

  Scenario: Delete a connection
    Given an existing connection
    When I delete it
    Then its credential is deleted and it no longer appears in the list

  Scenario: Non-administrator access
    Given I have only the "user" platform role
    When I call a connection management API
    Then access is denied

  Scenario: Untrusted host
    Given a user attempts to submit an arbitrary URL or localhost as an organization
    Then the API rejects the organization before any outbound HTTP request

  Scenario: Offline backend tests
    Given fake HTTP handlers and fake secret stores
    When xUnit runs
    Then no live GitHub or Azure DevOps PAT is needed
