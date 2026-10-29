Feature: Source security evidence, not speculative GitHub permissions

  Background:
    Given an Azure DevOps Services source connection is saved
    And I am signed in as a platform administrator

  Scenario: Capture groups and direct memberships
    Given the Graph groups endpoint has multiple pages
    And group A includes an Azure DevOps user and group B
    When I run discovery
    Then the snapshot stores Graph descriptors and direct membership edges
    And it does not flatten group B into a proven effective grant
    And the snapshot stores no PAT

  Scenario: Record project and repository ACEs
    Given Git repository security ACLs exist at a project and repository scope
    When the read-only ACL query runs
    Then the snapshot contains the original token, descriptor, Allow/Deny bitmasks and inheritance flag
    And GitHub permission assignments remain empty
    And no Azure DevOps permission update endpoint is called

  Scenario: Missing high privilege security scope
    Given the ACL service rejects a PAT with Forbidden
    When discovery completes
    Then the ACL coverage flag is incomplete
    And the snapshot includes a safe InsufficientScope warning
    And existing project, Git and user inventory remains available

  Scenario: Unknown security principal
    Given an ACL descriptor has no matching Graph legacyDescriptor
    When viewing the security evidence
    Then the original descriptor is displayed as unresolved evidence
    And it is never matched to an account by display name

  Scenario: Inherited access and Deny
    Given project-level Allow and repository-level Deny ACEs exist
    When I inspect the security evidence
    Then project and repository scope remain separate
    And effective permission verification remains false
    And the migration planner never treats the absent verified access list as confirmed orphaned users

  Scenario: Too large or inconsistent response
    Given group/member or ACE discovery reaches its safety limit or returns another token
    Then the relevant coverage is marked incomplete
    And the scan emits a warning rather than silently claiming total coverage
