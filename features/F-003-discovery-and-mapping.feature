Feature: Provider-neutral Azure DevOps discovery (F-003)
  Background:
    Given an authorized migration administrator
    And an Azure DevOps Services source connection with a saved PAT

  Scenario: Discover visible projects and Git references
    Given the Azure DevOps Services projects endpoint returns multiple pages
    And a project has more than one Git repository with branches and tags
    When I start discovery on the saved source connection
    Then I receive a new immutable canonical snapshot
    And its projects, repositories and refs contain original source IDs and object IDs
    And only read-only provider APIs have been called
    And the snapshot and API response contain no PAT

  Scenario: Discover TFVC root paths
    Given a project reports TFVC source control
    When discovery reads TFVC branch roots and children
    Then those branch paths and project root appear as TFVC source paths
    And the snapshot warns that unbranched paths and changeset history are not yet collected

  Scenario: Graph users are optional but coverage must be explicit
    Given I can read projects but Graph user API returns Forbidden
    When discovery completes
    Then the snapshot is marked partial
    And it contains an InsufficientScope issue
    And IdentitiesComplete is false

  Scenario: Repository permissions are not yet collected
    Given a source snapshot
    Then PermissionsComplete is false
    And the snapshot contains a PermissionsNotCollected warning
    And no proposed GitHub repository permissions are inferred from the user list

  Scenario: Repeated discovery preserves history
    Given a saved snapshot
    When I run discovery again
    Then the new snapshot has a different immutable ID
    And I can still open the original snapshot

  Scenario: Missing secret or destination connection
    When an attempt is made to discover a connection without a secret or a source provider
    Then the API refuses the request without storing an empty snapshot

  Scenario: Role protection
    Given a platform user without admin or superadmin
    When they start or open discovery
    Then the API rejects the request
