Feature: F-038 physical Git bundle integrity and storage safety
  As an operator
  I need PostgreSQL evidence tied to the real immutable Git bundle bytes
  So that a corrupted file or worker crash cannot be accepted as valid data

  Scenario: Revalidate an existing source bundle on every actual reuse
    Given a completed F-035 source bundle with a PostgreSQL SHA-256
    When the operator or worker attempts to reuse it
    Then its full bytes and length are checked against the immutable digest
    And an absent or modified file fails closed without a GitHub write

  Scenario: Recover a promoted bundle after crash before DB commit
    Given an atomically promoted F-035 .bundle file without its DB checkpoint
    When a retry receives the same exact source-ref proof
    Then the file is rehashed and Git bundle verify checks every branch and tag
    And the existing bytes can be recorded without overwriting the bundle

  Scenario: Respect a bounded store quota
    Given a configured max bundle store size
    When an export would exceed it
    Then temporary output is deleted and no complete bundle is promoted

  Scenario: Cleanup is conservative
    Given recognized temporary files older than 24 hours
    When a privileged operator invokes the internal stale-temp cleanup
    Then only expired recognized temporary files are deleted
    And every bundle and unknown file is preserved

  Scenario: Storage check requires authenticated administrator
    Given a F-035 bundle proof whose local storage is unavailable
    When a normal user requests the disk attestation endpoint
    Then authorization denies the request
    And an administrator receives an explicit storage-integrity error
