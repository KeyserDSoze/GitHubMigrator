Feature: F-095 durable, non-executable ancestry audit ledger
  Bound offline F-094 Git ancestry evidence to an independently verified
  F-091 baseline source checkpoint and an F-031 GitHub observation.

  Scenario: An offline ancestry proof is persisted atomically
    Given a current F-029 plan with sealed immutable F-014 Git refs
    And a verified version-2 F-091 baseline checkpoint
    And a current F-031 GitHub target observation for the mapped repository
    When an internal worker performs the disabled-by-default F-094 local audit
    Then the ledger atomically records the full audit receipt and one Recorded event
    And the receipt binds the checkpoint ID and attestation, exact source OIDs,
      target observation ID and SHA, operation manifest and local object sets
    And CanPush, CanRetry, CanDispatch and MigrationComplete remain false

  Scenario: Restart and duplicate submit are idempotent
    Given a committed ledger entry
    When another worker repeats the same F-094 proof and physical graph
    Then the existing ledger identity and receipt are returned
    And no duplicate Recorded event is committed
    And a different local graph for the same proof key is refused

  Scenario: Unsafe or partial evidence cannot be registered
    When the source ref inventory is partial, legacy-inline or unsealed
      or a checkpoint is legacy F-090, superseded or not the F-029 baseline
      or the destination observation is stale, mismatched or from another mapping
    Then no F-095 audit receipt is stored
    And the PostgreSQL ledger and event tables remain append-only

  Scenario: No remote execution authority is reachable
    Given the F-095 ledger has a valid fast-forward candidate
    Then the result is only local historical evidence, never permission to push
    And no remote network transport, retry, target mutation or migration completion is performed
