Feature: Select and approve repository migration plan
  Scenario: Externalized Git refs are counted from sealed PostgreSQL source evidence
    Given a successful leased discovery whose snapshot externalizes Git ref arrays
    And a matching immutable source-page manifest exists
    When the operator previews a selected repository
    Then its ref count comes from the same snapshot's verified PostgreSQL observations
    And altered page or object-ID evidence blocks preview instead of returning zero refs
    And no GitHub target permission or repository is created

  Scenario: Legacy source snapshots remain supported
    Given an immutable snapshot with inline Git refs and no externalized manifest
    When the operator previews a selected repository
    Then PostgreSQL groups only that snapshot's historical JSONB ref records
    And the plan never substitutes the newer canonical source connection catalog

  Scenario: Select repositories and rename targets
    Given a discovery snapshot contains multiple projects and repositories
    When an operator includes one repository and changes its destination name
    Then source identifiers and branch metadata remain unchanged
    And conflicts with existing destination repositories are reported before approval

  Scenario: Dry-run permission preview
    Given a plan with mapped and orphaned users
    When the operator previews the target organization
    Then the planned GitHub repository names, permission assignments and blocking warnings appear
    And no destination repository is written

  Scenario: Approve a versioned plan
    Given all blocking validations pass
    When the administrator explicitly approves the plan
    Then its snapshot and version are frozen for migration

  Scenario: GitHub organization access remains evidence, not a write authorization
    Given a saved verified GitHub Enterprise Cloud destination and authenticated login
    When the administrator checks organization access
    Then the adapter reads self-membership and private repository creation policy using only GET
    And the identity and organization in GitHub's response match the saved connection
    And pending or unavailable membership cannot be treated as active
    And org owner/member status and policy do not prove token repository-create permission
    And no plan is automatically approved

  Scenario: Target GitHub preflight distinguishes occupied from unverified names
    Given a selected migration plan with verified destination connection
    When an administrator checks target repository names
    Then only read-only GitHub GET requests are sent
    And a visible existing target repository is reported as a collision
    And a GitHub 404 is reported as not visible or missing, not as available
    And the plan cannot yet be approved or executed
