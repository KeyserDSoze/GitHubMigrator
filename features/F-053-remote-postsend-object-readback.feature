Feature: F-053 safe GitHub object tip readback
  To avoid confusing ref matches with complete Git objects
  As a migration operator
  I need bounded post-send readback without any implicit write authority

  Scenario: Exact created refs and object tips are observed
    Given a canonical set of missing create-only refs
    And complete GitHub ref snapshots that remain unchanged
    When the reader fetches commits and annotated tag objects via GET
    Then the inspection can report ExactRemoteRefsAndTipObjectsObserved
    And no automatic retry or remote write authorization is issued

  Scenario: Remote commit or tag evidence is unsafe
    Given an unavailable, wrong-SHA, invalid-type or cyclic Git object
    When the tip readback queries GitHub Git-data endpoints
    Then the check fails closed without a success receipt

  Scenario: Remote refs move while object tips are queried
    Given a first full GitHub ref observation
    And a different complete ref fingerprint after tip reads
    When the reader seals the evidence
    Then it refuses the readback

  Scenario: An unrelated GitHub ref appears
    Given independently fetched candidate object tips
    And a full ref set with an unexpected ref
    When F-053 recomputes the F-052 ref comparison
    Then the assessment requires operator reconciliation
    And blind retries remain forbidden
