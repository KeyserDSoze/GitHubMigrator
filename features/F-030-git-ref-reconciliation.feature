Feature: Read-only Git branch and tag reconciliation (F-030)
  As a migration operator
  I need a reproducible, bounded comparison between proven source and target Git refs
  So that incremental changes can be reviewed without authorizing target writes

  Scenario: Equal source and target ref object IDs
    Given immutable source and target observation proofs bound to an F-029 plan
    When two refs have the same fully-qualified name and Git object ID
    Then the comparison records MatchingObjectId
    And the reconciliation stays not ready for execution

  Scenario: Missing target ref is a candidate only
    Given a fully qualified source Git branch that has no target observation
    When the comparison is built
    Then it records CreateCandidate with DestinationWritePermissionUnverified
    And no GitHub mutation is issued

  Scenario: Different object IDs are not automatically safe fast forwards
    Given a source and target ref with different Git object IDs
    When the comparison is built
    Then it records DivergedBlocked with RefFastForwardUnverified
    And no overwrite or deletion is authorized

  Scenario: Target-only refs are preserved
    Given a target-only Git branch or tag
    When the comparison is built
    Then it records TargetOnlyPreserved
    And it does not infer source deletion from absence

  Scenario: Unsafe or ambiguous evidence fails closed
    Given duplicate or case-colliding ref names or mixed Git object formats
    When the comparison is built
    Then it rejects the request with a stable machine-readable code

  Scenario: Replay is deterministic with provenance
    Given an F-029 manifest digest and source and target ref evidence digests
    When the same refs are submitted in a different order
    Then the same ordered comparison and SHA-256 are produced
    And modifying any proof or ref changes the SHA-256
