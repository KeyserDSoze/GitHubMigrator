Feature: Provider-neutral discovery and identity mapping
  Scenario: Inventory source
    Given a configured source provider with read permissions
    When the operator starts discovery
    Then projects, Git repositories, TFVC paths, identities and access grants are captured as a versioned snapshot
    And the source PAT is never included in that snapshot

  Scenario: Preview orphaned permissions
    Given a source repository readable only by unmapped source identities
    When I preview target permissions
    Then the repository is marked as having orphaned access
    And I may explicitly add mapped target users before approval

  Scenario: Rename destination
    Given a discovered source repository
    When I choose a different target repository name
    Then its source identity and original history remain unchanged
    And a target-name conflict is detected before execution
