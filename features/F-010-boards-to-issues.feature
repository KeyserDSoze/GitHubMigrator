Feature: Map Azure Boards work items to GitHub Issues and Projects
  Scenario: Preserve source hierarchy
    Given epics, features, stories, tasks and parent links in a source board
    When an operator selects an explicit GitHub mapping strategy
    Then the target issue relationships and project fields are previewed
    And source IDs remain present as traceable references

  Scenario: Unsupported custom fields
    Given boards contain custom process templates, area paths and iterations
    When preview runs
    Then unsupported fields and customizations are reported
    And the operator must approve lossy transformations
