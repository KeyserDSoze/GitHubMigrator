Feature: F-075 PostgreSQL-authoritative write-disabled GitHub dispatch review
  Prepared GitHub create-only review evidence is not usable if a
  competing worker has changed its lease, approval or possible-send
  state in PostgreSQL.

  Scenario: Locked authority produces a read-only attestation
    Given a source-bound F-044 packet and matching F-051 remote preflight
    And F-045, F-047, F-050, F-052 and F-055 control rows exist in PostgreSQL
    And durable independent-review and initial-hazard audit events are intact
    And the existing preview lease is Held and unexpired
    When the F-075 verifier acquires both repository advisory locks
    And rereads all control records using the PostgreSQL server clock
    Then F-074 produces only ReviewedWriteDisabled SHA-256 review evidence
    And no GitHub push, remote authorization, retry or completion is possible

  Scenario Outline: Mutated persisted authority invalidates an earlier proof
    Given a previously valid pre-dispatch review
    When <change> is committed to the PostgreSQL control plane
    Then a new verifier call rejects the stale authority
    And no new send is scheduled

    Examples:
      | change |
      | F-045 lease expires |
      | F-045 lease token is replaced |
      | F-045 lease is released |
      | F-050 fence requires recovery |
      | F-052 grant is cancelled |
      | F-055 possible transmission is marked |
      | F-055 hazard requires operator review |

  Scenario: Missing durable audit events block a plausibly good row
    Given matching F-052 and F-055 state rows without append-only audit events
    When the F-075 verifier examines the matching epoch
    Then the review is refused even when the row state says write disabled

  Scenario: A competing possible-send marker wins the repository lock
    Given a valid reviewed attempt and prepared hazard
    And another worker holds the F-055 numeric repository lock
    When that worker commits MayHaveTransmitted
    And the waiting verifier acquires the repository lock afterwards
    Then the verifier reads the marked hazard and refuses dispatch review
    And no retry, GitHub send or remote mutation occurs

  Scenario: Multiple replicas read the same epoch without changing it
    Given an unchanged valid PostgreSQL authority snapshot
    When six replicas independently validate the same repository
    Then their write-disabled SHA-256 review receipts agree
    And no duplicate hazard or approval events are created
