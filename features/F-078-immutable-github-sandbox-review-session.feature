Feature: F-078 immutable write-disabled GitHub sandbox review session
  A verified remote sandbox rehearsal must produce a durable record of
  its exact source, GitHub observations and PostgreSQL authority epoch
  without being mistaken for a command to push code.

  Scenario: Trusted sandbox review produces exactly one immutable session
    Given F-044 canonical source evidence and the physical bundle are valid
    And the F-052 reviewed F-051 SHA is retained by the immutable F-077 journal
    And F-072/F-073 report stable GitHub ref and policy observations
    And F-075 confirms F-045/F-047/F-050/F-052/F-055 under both locks
    When the internal F-078 session recorder re-acquires both locks
    Then it stores a SHA-256 RecordedWriteDisabled session and one audit event
    And no GitHub push, retry permission or migration-complete flag is enabled

  Scenario: Replicas racing to store one session are exactly idempotent
    Given a valid F-055 hazard and an F-074 read-only review
    When six independent replicas try to record the same session
    Then exactly one session and one immutable audit event exist
    And the other replicas see the same receipt rather than new sessions
    And a different observer or review digest for that hazard is refused

  Scenario Outline: Persisted record is immutable even under direct SQL
    Given an F-078 session was recorded
    When SQL attempts <operation>
    Then PostgreSQL refuses the mutation
    And the original session and its audit event remain available

    Examples:
      | operation |
      | set remote_writes_authorized to true |
      | replace the SHA-256 review proof |
      | delete session |
      | delete session audit event |

  Scenario: A possible transmission committed under F-055 lock blocks recording
    Given an independently reviewed write-disabled attempt
    And another worker holds the F-055 numeric repository lock
    When that worker commits MayHaveTransmitted
    And the F-078 recorder obtains the same lock afterwards
    Then no new session is recorded
    And the recorded hazard cannot be retried automatically

  Scenario: Previously recorded session becomes historical only
    Given an immutable RecordedWriteDisabled session exists
    When its lease expires or the F-055 hazard becomes MayHaveTransmitted
    Then inspection shows HistoricalOnly
    And the session remains readable for audit
    And CanExecute, CanRetry and MigrationComplete are all false

  Scenario: Missing authenticated preflight history blocks recording
    Given F-075 validated the PostgreSQL control-plane authority
    But the F-077 exact F-051 reviewed proof row is absent
    When F-078 attempts to record the session
    Then the recording is refused
    And no session or audit event is created
