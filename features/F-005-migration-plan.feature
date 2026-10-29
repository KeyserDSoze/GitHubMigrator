Feature: Select and approve repository migration plan
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
