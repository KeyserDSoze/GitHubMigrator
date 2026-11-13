Feature: F-094 offline ancestry and object-integrity proof for incremental Git refs
  Compare two self-contained trusted local bare repositories with real Git,
  never treating a SHA-256 staging fingerprint as a Git object ID.

  Scenario: Local branches have known commit graph relationships
    Given a complete source mirror and a complete target bare sandbox
    And exact canonical refs from already sealed source and target proofs
    When the offline Git auditor checks each branch
    Then identical objects, fast-forward candidates, target-ahead, divergence
      and missing historical objects are distinguished
    And a fast-forward candidate remains blocked for remote execution
    And every decision is SHA-bound to both proof identities and object sets

  Scenario: Tags and unrelated target refs are preserved
    Given an annotated tag exists in both local repositories
    When the object IDs differ
    Then the result is TagChangeBlocked, not an automatic force update
    And a target-only ref is classified as TargetOnlyPreserved

  Scenario: Unsafe local Git evidence refuses the entire audit
    When a repository is shallow, uses alternates or promisor files,
      has changed refs, missing objects, invalid SHA format or symlink paths
    Then no successful offline receipt is produced
    And no outbound network action, GitHub token, git push or migration
      completion is possible
