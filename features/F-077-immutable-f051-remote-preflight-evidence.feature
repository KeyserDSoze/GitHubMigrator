Feature: F-077 immutable historical GitHub App preflight evidence
  The F-051 prior read-only preflight used by the F-075 dispatcher
  must be reconstructed from the exact F-052 reviewed PostgreSQL
  evidence, not accepted as a caller-selected in-memory object.

  Scenario: Authenticated F-051 preflight is appended before API response
    Given F-072/F-073 has read the expected GitHub App installation and refs
    And the F-050 repository fence is still reviewed and unexpired
    When F-051 finishes sealing its SHA-256 receipt
    Then F-077 records the receipt and original policy observation timestamp
    And one immutable Recorded audit event is appended transactionally
    And the F-051 caller receives the receipt only after persistence succeeds
    And no token or GitHub raw refs are persisted by F-077

  Scenario: Multiple preflights in the same generation retain separate proof
    Given F-052 proposal and independent review each run a fresh F-051 GET
    When both preflights have distinct evidence SHA values
    Then each receipt is preserved with its own immutable audit event
    And neither receipt replaces the other
    And F-076 selects the SHA recorded by the completed F-052 review

  Scenario: Six concurrent F-051 receipt replays are idempotent
    Given six replicas record the same request, generation and evidence SHA
    When PostgreSQL serializes them with the F-050 advisory lock
    Then exactly one append-only evidence row exists
    And exactly one Recorded audit event exists
    And all callers observe the same cryptographic proof

  Scenario Outline: Tampering cannot alter historical evidence
    Given a valid immutable F-051 receipt was appended
    When an actor tries <mutation>
    Then PostgreSQL rejects the operation
    And the original receipt and audit event remain unchanged

    Examples:
      | mutation |
      | UPDATE current policy digest |
      | UPDATE execution_authorized to true |
      | DELETE receipt |
      | DELETE audit event |

  Scenario: F-050 moves to recovery before recording
    Given a fresh previously valid F-051 receipt
    When F-050 changes to RecoveryRequired before the journal lock
    Then F-077 refuses to append new evidence
    And the resulting API request fails closed

  Scenario: F-055 possible transmission blocks trusted proof retrieval
    Given F-052 references the reviewed historic F-051 evidence SHA
    And an earlier proposal SHA exists in the same epoch
    When F-055 marks MayHaveTransmitted
    Then the F-076 journal-bound entrypoint refuses review
    And neither an ambiguous send nor any old receipt can authorize retry
