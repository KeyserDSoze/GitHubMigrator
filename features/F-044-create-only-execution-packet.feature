Feature: F-044 sealed create-only Git execution packet (read-only)
  As a superadmin preparing a GitHub migration
  I need a deterministic candidate-only proof from immutable evidence
  So that no future worker can infer executable authority from a dry run

  Scenario: Exact local rehearsal and remote observation produce a stable packet
    Given a current F-043 scoped token attestation and F-042 unchanged ref fence
    And a two-person F-040 acknowledgement and real F-041 Git rehearsal
    And complete F-032 comparisons plus an untampered F-038 disk bundle
    When the superadmin requests the F-044 packet preview
    Then the response contains a stable SHA-256 and create-only candidate digest
    And it contains no Git command, ref name, token, PEM or filesystem path
    And every execution and remote-write flag is false

  Scenario: Altered source refs or old Git proof fail closed
    Given F-041 created exactly one missing branch and preserved target-only refs
    When a candidate object ID changes or a source ref is reclassified
    Then the packet fails without a remote write

  Scenario: Replayed or stale proofs never unlock a writer
    Given the app token has been discarded and remote state may have drifted
    When a stale F-042 fence, different reviewer or forged source SHA is presented
    Then the review packet fails closed
    And no GitHub push or branch mutation is attempted
