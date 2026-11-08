Feature: F-051 GitHub App single-repository ephemeral preflight
  As a GitHub migration operator
  I need current remote reference and policy evidence using the exact GitHub App identity
  Without issuing a push or leaking the scoped installation token

  Scenario: Ephemeral single-repository GitHub App token stays inside infrastructure
    Given a F-043 App ID and a protected signing key
    And a fresh F-044 packet and reviewed, write-disabled F-050 fence
    When the remote preflight runs
    Then only the GitHub installation token mint call may use POST
    And the ref/ruleset/repository observations are GET-only
    And no API payload, receipt or audit record contains a bearer token

  Scenario: Remote refs change while a packet is prepared
    Given a previously observed full target ref-set fingerprint
    When a new branch, tag or changed OID appears on GitHub
    Then preflight reports RemotePreflightTargetDriftBlocked
    And it cannot authorize or attempt an automatic retry

  Scenario: GitHub App is too broadly scoped
    Given a token response listing multiple repositories or admin permission
    When the mint response is checked
    Then the preflight is refused before querying remote refs
    And no GitHub repository mutation is possible

  Scenario: Reviewed F-050 write fence expires during network calls
    Given the F-050 fence was ReviewedWriteDisabled
    When its deadline or generation changes during GitHub GETs
    Then the receipt is refused, even with unchanged remote refs
    And the F-047 permanent GitHub write barrier remains closed
