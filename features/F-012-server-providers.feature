Feature: Extend provider adapters to on-premises and enterprise server instances
  Scenario: Azure DevOps Server/TFS connection
    Given an operator configures a server and collection
    When endpoint security checks and version negotiation complete
    Then supported authentication modes and API capabilities are reported
    And private or arbitrary network targets require an explicitly configured egress policy

  Scenario: GitHub Enterprise Server destination
    Given a trusted GitHub Enterprise Server instance and organization
    When the destination provider validates credentials
    Then it checks that server's own API and auth capabilities
    And the existing canonical migration plan remains provider-neutral
