Feature: F-058 immutable independent GitHub object-copy reconciliation journal
  As a migration reviewer
  I want a durable SHA-bound link from a read-only full-history proof
  To the original reviewed grant, typed refs and possible-send marker
  Without granting transport, automatic retry or migration completion

  Scenario: Six replicas record the same full-history observation
    Given a historical F-047 blocked attempt and F-050/F-052 reviewed intent
    And a sealed F-054 ref-tip readback and F-055 MayHaveTransmitted marker
    And a fresh F-057 full-object mirror receipt with exact refs
    When six PostgreSQL replicas record the same observation
    Then exactly one immutable evidence row and audit event are written
    And all replicas receive the same immutable ID
    And the F-055 possible-transmission marker is unchanged

  Scenario: Source and target references must match the reviewed epoch
    Given an original typed-tag baseline and create-only candidate set
    When a remote ref is unexpectedly added or the closure SHA is forged
    Then the evidence is refused and no safe-retry claim is issued

  Scenario: A historical reviewed grant can expire while full-history review continues
    Given the F-052 short-lived review intent has expired
    And its permanent no-write barriers are unchanged
    When a valid F-057 independent observation is persisted
    Then no new execution authority is created

  Scenario: A grant or hazard comes from another repository
    Given otherwise plausible proof hashes
    When direct SQL links a different repository, grant or F-055 hazard
    Then PostgreSQL composite foreign keys reject the evidence

  Scenario: No remote write or retry can be authorized through an audit receipt
    Given an F-058 evidence record
    When direct SQL tries to update its hashes, state, migration completion or flags
    Then PostgreSQL rejects the write and audit event modification
