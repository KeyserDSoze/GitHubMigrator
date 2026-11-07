Feature: F-074 cryptographically sealed GitHub create-only pre-dispatch review
  A safe remote test must not mistake scoped credentials, checked refs or
  an approved write-disabled grant for actual write authorization.

  Scenario: Four independent evidence chains agree without authorizing transport
    Given an F-044 immutable canonical create-only candidate packet
    And F-051 fresh GitHub App read-only remote preflight evidence
    And an F-052 independently reviewed write-disabled test grant
    And an F-055 prepared, unmarked hazard matching that exact generation
    And a new, complete, typed GitHub ref observation matching the baseline
    When the F-074 domain validator seals the proof
    Then only a short-lived SHA-256 review receipt is produced
    And the receipt contains no token, refspec, GitHub URL or Git command
    And remote transport, remote authorization, auto-retry and migration-complete
      remain false

  Scenario Outline: Mismatched evidence prevents a pre-dispatch receipt
    Given the four persisted proof chains and latest typed target refs
    When <mutation> changes relative to the verified packet
    Then F-074 fails closed with a sanitized error
    And it does not authorize a remote GitHub mutation

    Examples:
      | mutation |
      | numeric destination repository ID |
      | source packet SHA |
      | canonical create-only candidate list |
      | reviewed original policy SHA |
      | F-050 fence generation |
      | independent grant reviewer |
      | expired preflight or grant |
      | expired installation token |
      | altered F-055 hazard epoch |

  Scenario Outline: A changed remote baseline refuses the review
    Given the F-051 snapshot of all heads and annotated tag object IDs
    When the latest GitHub GET changes <remote_ref>
    Then F-074 rejects the baseline as drifted
    And the existing target ref must not be overwritten

    Examples:
      | remote_ref |
      | existing branch |
      | annotated tag object |
      | newly occupied create-only branch |
      | an unrelated target-only ref |

  Scenario: An ambiguous prior send is never retried
    Given an F-055 journal that is MayHaveTransmitted or OperatorReviewRequired
    When the latest read-only GitHub observation appears to match the baseline
    Then F-074 refuses to produce a pre-dispatch review
    And MayAutomaticallyRetry remains false
    And human reconciliation must precede any future independently authorized
      execution implementation
