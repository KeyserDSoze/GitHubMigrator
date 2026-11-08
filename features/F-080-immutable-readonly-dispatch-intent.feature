Feature: PostgreSQL immutable write-disabled handoff for reviewed GitHub sandbox sessions
  A manually reviewed sandbox session may be recorded for future audit,
  but that record must never be executable or an automatic retry.

  Scenario: Immutable independent actor handoff
    Given F-078 has stored a current trusted session
    And the F-055 hazard is still PreparedWriteDisabled
    And the approved F-052 grant, F-050 fence and F-045 lease remain current
    When a distinct superadmin requests an F-080 handoff
    Then PostgreSQL locks the exact numeric repository before rechecking authority
    And one hash-sealed intent and one immutable audit event are created
    And the intent contains no refspec, credentials or transport permission

  Scenario: Repeated requests from six replicas are idempotent
    Given a current trusted session has one F-055 hazard
    When six PostgreSQL connections record the same actor and session
    Then they return the exact same F-080 intent and SHA
    And there is only one intent and one audit event
    When another actor attempts to overwrite the handoff
    Then the request fails without changing either record

  Scenario: A marked hazard permanently blocks new handoffs
    Given a current F-080 intent exists
    When F-055 is marked MayHaveTransmitted
    Then the intent becomes HistoricalOnly
    And any retry of the record operation is rejected
    And no API offers sending, execution, automatic retry or completion

  Scenario: A competing hazard marker wins the repository lock
    Given F-055's repository lock is held by another transaction
    And that transaction marks a possible transmission before committing
    When a waiting F-080 recorder obtains the lock
    Then its database-clock authority check rejects the handoff
    And no F-080 audit event exists

  Scenario: Tampering and cross-scope readback are forbidden
    Given a stored F-080 record and one event
    When a SQL client updates the execute flag, hash or deletes the evidence
    Then PostgreSQL rejects the operation
    When a superadmin requests the intent under a different plan or scope
    Then the route does not expose the record

  Scenario: Disabled and unauthorized HTTP requests remain offline
    Given the F-080 feature is disabled by default
    When an anonymous user or a non-superadmin tries to create an intent
    Then access is denied
    When a superadmin tries to create or inspect an intent
    Then the API fails closed with SandboxDispatchIntentDisabled
    And no GitHub provider network request is made
