Feature: Atomic pre-send hazard barrier against ambiguous GitHub transport
  A reviewed F-081 confirmation can be consumed at most once.
  The database records pessimistic uncertainty before any future send,
  without granting execution rights.

  Scenario: Exact independently reviewed epoch is consumed atomically
    Given F-081, F-080 and F-078 have immutable fresh evidence
    And F-055 is still PreparedWriteDisabled
    And the new operator differs from all prior reviewers and preparers
    When an internal F-082 caller records a possible send
    Then PostgreSQL locks the exact numeric repository
    And the F-055 MayHaveTransmitted transition and event commit together
    And exactly one immutable F-082 record and audit event commit
    And no GitHub transport is invoked

  Scenario: Six replicas may not double-send
    Given six independent PostgreSQL connections request the same epoch
    When they race on the repository advisory locks
    Then exactly one can record the irreversible hazard
    And every other caller is refused even with the same actor
    And no automatic retry is possible

  Scenario: Another operator wins the hazard lock first
    Given a F-082 recorder waits on the F-055 repository lock
    When a competing transaction commits MayHaveTransmitted
    Then the F-082 recorder refuses the stale confirmation
    And no F-082 evidence is appended

  Scenario: Actor, plan, scope and authority are strictly bound
    Given a current independently confirmed sandbox review
    When its previous confirmer acts as the new marker
    Then the request is refused
    When a caller supplies a different preparation or plan or scope
    Then the request is refused with no F-055 state mutation

  Scenario: Evidence is permanent yet not executable
    Given F-082 has atomically recorded a possible send
    When an operator reads the exact scoped record
    Then it has HistoricalOnly status and a valid SHA and audit trail
    And CanExecute, CanRetry and MigrationComplete are false
    When SQL updates the flags or deletes the record or audit
    Then PostgreSQL rejects the mutation

  Scenario: No sender, credential or HTTP mutation path
    Given F-082 is disabled by default and not exposed as an API route
    When the API starts or an unrelated request is processed
    Then no GitHub outbound write can occur from F-082
