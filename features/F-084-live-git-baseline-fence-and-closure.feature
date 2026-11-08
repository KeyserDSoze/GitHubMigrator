Feature: Live Git ref fence before atomic sandbox push and physical closure proof
  Even after two authenticated GitHub REST snapshots, the target may change.
  This final fence fails closed and cannot make F-082 repeatable.

  Scenario: Real Git creates only missing SHA-1 branch and annotated tag
    Given a SHA-1 source bundle and a local bare target sharing the main commit
    And F-083 has consumed the irreversible F-082 possible-send marker
    When the F-083 writer verifies the exact Git remote refs and pushes once
    Then only the feature branch and annotated tag are created atomically
    And the existing main ref remains unchanged
    And an independent F-057 Git fetch verifies full object closure
    And no migration-complete or automatic-retry capability is issued

  Scenario: SHA-256 object format is covered as well
    Given a SHA-256 bare source and local bare target
    When F-083 pushes absent refs and F-057 independently re-fetches
    Then the remote ref set and every reachable object SHA are verified

  Scenario: Candidate or unrelated remote ref changes after F-082
    Given a valid sealed F-083 baseline
    And a competing actor creates a candidate or unrelated branch
    When the Git subprocess performs its last ls-remote fence
    Then Git refuses to push with SandboxPushRemoteBaselineChanged
    And the competing ref is not overwritten or deleted
    And the F-082 epoch cannot be retried

  Scenario: A forged or stale typed baseline is refused before Git
    Given an F-082 immutable marker and approved numeric test repository
    When a caller changes a typed baseline ref, its SHA or its timestamp
    Then the writer rejects the request without any outbound Git command

  Scenario: There is no public remote migration launch control
    Given F-084 is stacked on the internal disabled-by-default F-083 sender
    Then no API or background worker triggers a GitHub.com push
    And live GitHub sandbox acceptance still requires explicit authorization
