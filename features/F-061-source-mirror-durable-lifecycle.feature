Feature: F-061 durable source mirror lifecycle with cross-worker leases
  As a GitHub migration operator
  I need physical source mirror proof persisted across worker crashes
  Without any remote sender, automatic retry or unverifiable success

  Scenario: Six workers attempt one source materialization
    Given a current F-038 source bundle and a frozen F-029 snapshot
    When six independent PostgreSQL replicas acquire the same physical mirror
    Then only one unexpired fenced lease is granted at a time
    And only its holder can persist a verified object-set digest

  Scenario: Worker dies after atomic Git rename but before database checkpoint
    Given a final bare Git mirror and an expired Leased SQL row
    When another worker claims a higher lease generation
    Then the previous generation can never record success
    And the new worker verifies every Git ref and reachable object again
    And only then can it checkpoint Verified in PostgreSQL

  Scenario: A verified Git mirror is corrupted
    Given a persisted Verified SHA proof
    When a new worker rechecks an altered Git object pack
    Then the lifecycle becomes RecoveryRequired
    And automatic acquisition is forbidden
    And the existing final repository is never silently repaired

  Scenario: Direct SQL attempts to enable remote execution
    Given a physical source mirror lifecycle row
    When SQL attempts to set remote_push_invoked, may_automatically_retry
      or migration_complete to true or modify audit events
    Then PostgreSQL refuses every unsafe mutation

  Scenario: Crash cleanup touches only recognizable old staging
    Given an old UUID-named staging directory, a new staging directory
      and a protected final bare Git repository
    When explicit cleanup runs under the source filesystem lock
    Then only the old recognized staging directory is removed
    And symlinks and unrelated directories are never followed or deleted
