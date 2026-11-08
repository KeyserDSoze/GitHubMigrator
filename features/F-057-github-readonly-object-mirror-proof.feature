Feature: F-057 independent remote read-only Git history proof
  As a migration operator
  I want to independently acquire and verify the GitHub target history
  Without authorizing any remote push or accepting only tip-level SHA proof

  Scenario: A fresh full target mirror matches the source object closure
    Given a SHA-bound F-031 remote ref observation and a known source bare snapshot
    And the exact numeric GitHub repository allowlist and short-lived credential
    When read-only Git ls-remote and fetch acquire a private bare target copy
    Then full Git fsck and reachable-object history match the source
    And the final remote ref observation remains exactly unchanged
    And the resulting receipt cannot authorize GitHub writes or completion

  Scenario: A remote changes since the approved observation
    Given an observed source and target ref set
    When a target branch tip changes before or during the fetch
    Then the proof is rejected and the private mirror removed

  Scenario: Annotated tag type is misreported
    Given a SHA-bound observation marking an annotated tag as a commit
    When the independent Git mirror exposes the physical object type
    Then the type mismatch is rejected without a success receipt

  Scenario: A remote URL or token injection is attempted
    Given an unapproved GitHub repository or control characters in a token
    When an audit is requested
    Then network transport is not attempted and no credential is persisted

  Scenario: Local sources are missing or scratch is a symlink
    Given an invalid trusted source handle or a symlinked scratch root
    When the independent read-only proof runs
    Then it fails closed and cleans up temporary resources
