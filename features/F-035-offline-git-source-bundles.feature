Feature: F-035 Git source bundle evidence bound to a blocked workpack
  As a migration operator
  I want an actual verified Git object bundle from a certified local source mirror
  So that I can later import the objects only after independent authorization

  Scenario: Export an exact Git object graph from a bare source
    Given a current F-032 source ref proof and blocked F-034 workpack
    And an operator-provisioned bare Git source containing only those refs
    When the source bundle acquirer runs
    Then git fsck verifies the object store
    And git bundle create exports complete Git object history
    And a fresh bare Git repository can import all branch, tag and commit objects
    And the bundle is hashed with SHA-256 and atomically saved

  Scenario: Source refs drift or hidden refs appear
    Given the operator-provisioned mirror differs from the immutable source proof
    When the acquirer enumerates all actual refs
    Then no completed bundle is emitted
    And no PostgreSQL source bundle checkpoint is recorded

  Scenario: Local-only safety
    Given the bundle feature is disabled or the trusted roots are missing
    When a caller requests source acquisition
    Then the request is rejected without opening a network connection

  Scenario: PostgreSQL evidence replay
    Given a SHA-256-bound bundle already exists for a current workpack
    When the same proof is recorded again
    Then the exact existing row is returned with no duplicate
    And attempts to change the binary checksum are rejected

  Scenario: SQL write authorization remains impossible
    Given an acquired source bundle has a matching SHA-256
    When a client tries to mark the evidence ready for execution
    Then PostgreSQL rejects the mutation
    And no GitHub push or ref update is invoked
