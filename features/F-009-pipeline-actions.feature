Feature: Convert pipelines to reviewed GitHub Actions
  Scenario: Assist with conversion
    Given a supported Azure DevOps pipeline and an approved AI provider configuration
    When the operator requests a suggested GitHub Actions workflow
    Then secrets, service connections and variable groups are redacted before the AI call
    And the proposed workflow and unsupported tasks are shown as a diff
    And no workflow is pushed without human approval

  Scenario: AI unavailable
    Given Foundry has no deployment or quota
    When a conversion is requested
    Then the feature offers a deterministic/manual fallback
    And the system does not silently claim equivalence
