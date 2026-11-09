Feature: F-096 bind F-094 Git ancestry to the verified F-060 physical mirror
  The logical F-029 snapshot ID is not the physical directory name.
  A workpack owns exactly one F-060 mirror with a sealed F-061 lifecycle.

  Scenario: An F-060 verified source mirror admits a local F-094 audit
    Given an F-091 attested baseline checkpoint
    And an F-014 verified complete source ref/OID set
    And an F-035 source bundle with a matching F-061 Verified lifecycle
    When F-095 records the offline Git ancestry audit
    Then F-094 reads the workpack-specific physical mirror UUID
    And its full source object SHA/count match the F-060 proof
    And the v2 ancestry SHA binds both physical mirror UUID and F-060 proof
    And no remote GitHub action or automatic retry occurs

  Scenario: Wrong mirror, lease, hash or repository is refused
    When the workpack ID is omitted, foreign, stale or unverified
    Or the mirror lifecycle is leased, quarantined or lacks a Verified event
    Or the ref set SHA, bundle SHA, object set SHA or mirror SHA changes
    Then nothing is recorded in the immutable F-095 ledger
    And no blind remote retry or write permission is produced

  Scenario: Historical proofs remain readable
    Given an F-094/F-095 receipt created before F-096
    Then its original v1 SHA remains verifiable
    And newer records use the v2 SHA-bound physical mirror instead

  Scenario: SHA-1 and SHA-256 Git repositories retain closure verification
    Given two isolated bare Git repositories
    When the F-094 auditor reads the workpack mirror by physical UUID
    Then real Git fsck and ancestry classification are performed
    And the logical snapshot path cannot masquerade as the physical mirror
