Feature: Map source identities to destination users
  Scenario: Map an Azure DevOps identity
    Given a discovery snapshot includes source identities with immutable provider IDs
    When an operator maps an identity to a GitHub login
    Then the map is stored independently of source PATs
    And the mapping can be previewed without granting GitHub access

  Scenario: Orphaned access warning
    Given an included repository has access grants only for unmapped identities
    When the permissions preview is generated
    Then its access is marked orphaned
    And the operator may add explicitly mapped GitHub principals

  Scenario: Nested groups and unsupported identities
    Given source permissions are inherited from groups
    When the target permission mapping is prepared
    Then effective grants, membership and unknown identities are reported separately
    And no access is silently granted to another user
