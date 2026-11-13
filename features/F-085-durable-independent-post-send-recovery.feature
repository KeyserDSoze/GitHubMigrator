Feature: Durable post-send recovery linked to independent GitHub object evidence
  A possible-send barrier must survive crash and never become a retry.
  Remote tips and complete object closure must be independently recorded.

  Scenario: Crash immediately after F-082 prevents replay forever
    Given F-081 was independently confirmed and F-082 was committed atomically
    When the sender crashes before recording an outcome
    Then the next process reads AwaitingIndependentReadback from PostgreSQL
    And CanRetry, CanDispatch and MigrationComplete are false
    And no HTTP action triggers a GitHub push

  Scenario: Read-only tips alone are insufficient
    Given F-082 marked a possible send
    When a distinct operator records F-054 exact remote tip evidence
    Then recovery reports TipsObservedNeedsClosure
    And an independent closure checkpoint is refused until F-058 exists

  Scenario: Complete physical object proof can be independently checkpointed
    Given F-054 exact ref/tip evidence was recorded after F-082
    And F-058 independently fetched and verified all Git objects
    When a new independent superadmin records a closure checkpoint
    Then the F-054, F-058 and F-082 identities and SHA-256 values are bound
    And a unique immutable F-085 event is committed
    And recovery reports CheckpointedIndependentClosure after process restart
    And no migration-complete or automatic-retry capability is issued

  Scenario: Concurrent recorder replicas do not duplicate evidence
    Given six PostgreSQL connections see the same valid independent closure
    When they record a checkpoint with the same independent actor
    Then one immutable checkpoint and one audit event are committed
    And all six calls return the same historical receipt

  Scenario: Corrupt or non-independent evidence is rejected
    Given F-082 was committed
    When the F-054 audit is missing or the F-058 full closure hash is forged
    Then recovery refuses to treat the remote state as fully verified
    And F-085 cannot record a closure checkpoint
    When the original sender attempts to certify its own remote proof
    Then the operation is refused

  Scenario: Role-gated read and body-free checkpoint API is not a sender
    Given F-085's exact numeric sandbox allowlist is disabled by default
    When an anonymous user or non-superadmin requests recovery
    Then authorization is refused
    When a superadmin requests recovery while disabled
    Then the request is rejected without GitHub network calls
    And no endpoint accepts a token, URL, source OID or dispatch command
