Feature: F-093 independently verified source-checkpoint vs GitHub target name review
  A source F-091 ref-fingerprint checkpoint and an F-031 read-only
  GitHub target ref observation can be compared without target writes.

  Scenario: Review canonical source Git ref changes against the target ref set
    Given a verified version-two checkpoint and a current F-031 target observation
    And their F-029 mapped source/target repository identities match
    When a superadmin reads an ordered page for one source repository
    Then GitHub ref names are classified as observed or absent at the F-031 observation time
    And source ref changes remain separately labeled Added, Changed, Restored or Missing
    And the response carries both original SHA-256 evidence proofs
    And CanPush, CanRetry, CanDispatch and MigrationComplete remain false

  Scenario: A changed source ref does not imply fast-forward
    When source metadata changes and its destination ref exists
    Then F-093 says "SourceChangedTargetPresentBlocked"
    And F-093 never equates an F-013 source fingerprint with a Git object ID

  Scenario: Stale or unrelated destination observations are rejected
    Given the F-031 observation is stale or predates the staging checkpoint
    When an operator requests an F-093 review
    Then the endpoint refuses without calling any external provider
    And a foreign source mapping or forged target evidence SHA also refuses

  Scenario: Historical and untrusted source evidence cannot authorize writes
    Given a legacy F-090 checkpoint without an F-091 fingerprint
    When a review is requested
    Then no comparison is produced
    And only a superadmin with a changed password can request the GET
