Feature: F-076 trusted source and live GitHub readback for write-disabled dispatch
  A future GitHub test import must not rely on caller-supplied Git refs,
  bearer tokens, repository URLs or stale in-memory workpacks.

  Scenario: Trusted read-only orchestration combines existing safeguards
    Given a server-configured GitHub App and one numeric repository allowlist
    And the prior F-051 preflight is bound to the F-052 reviewed grant
    When the orchestrator reconstructs the F-044 packet from the source records
    And verifies the physical Git bundle and source ref journal
    And the F-072/F-073 authenticated GitHub reader rechecks the live target
    And the candidate policy checks report no active rules
    And the same F-044 source evidence is rebuilt after the remote GETs
    And F-075 validates the PostgreSQL authority under both repository locks
    Then only a SHA-256 ReviewedWriteDisabled receipt is returned
    And no push, remote write permission, automatic retry or completion is possible

  Scenario Outline: Wrong source or preflight blocks credential access
    Given an otherwise valid source migration proof
    When <mutation> is detected before GitHub communication
    Then no App signing key is read and no token is minted
    And no authority review is executed

    Examples:
      | mutation |
      | wrong F-044 packet digest |
      | wrong numeric repository |
      | wrong original policy |
      | unverified F-051 preflight |
      | disabled test mode |
      | unallowlisted target |

  Scenario Outline: Changed target or provider policies fail closed
    Given the private signing key is available only to the authenticated reader
    When GitHub GETs reveal <mutation>
    Then the orchestrator refuses the proof
    And no PostgreSQL review receipt is issued

    Examples:
      | mutation |
      | GitHub repository ID changed |
      | installation ID changed |
      | branch OID changed |
      | annotated tag object OID changed |
      | candidate ref already exists |
      | unrelated target-only ref appeared |
      | active candidate branch rules |
      | unverified candidate rules |

  Scenario: A source bundle or candidate set changes during the GitHub GETs
    Given the first F-044 source reconstruction is valid
    When the GitHub App reader is inspecting the target
    And the server-side source evidence changes before the second reconstruction
    Then the orchestrator rejects source drift
    And F-075 does not issue a write-disabled readiness receipt

  Scenario: An already possible transmission veto cannot be bypassed
    Given the F-055 hazard was marked MayHaveTransmitted in PostgreSQL
    And F-044 and the latest GitHub GET still look consistent
    When the orchestrator delegates to the F-075 locked verifier
    Then persisted authority refuses the review
    And no attempt is retried or marked complete
