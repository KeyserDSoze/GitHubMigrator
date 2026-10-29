Feature: Repository foundation and local development
  Scenario: Portable local services
    Given Docker and Compose are installed
    And I have a copied .env.local file
    When I run the Linux or PowerShell up script
    Then the API, React UI, PostgreSQL, Azurite and Grafana LGTM start
    And I can change every host port independently

  Scenario: Committed test environment is free of production credentials
    Given the .env.test file is tracked
    Then it contains only documented non-secret test values
    And .env.local, .env, real PATs, Terraform state and real tfvars are ignored

  Scenario: Architecture quality gates
    Given a change on main or in a pull request
    When CI runs
    Then .NET xUnit tests, frontend build, Playwright tests and Terraform validation run
