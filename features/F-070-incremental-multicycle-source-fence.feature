Feature: F-070 atomic admission and durable multi-cycle source sync
  Incremental source preparations must stay ordered when new snapshots
  arrive and after a worker crashes during a later change cycle.

  Scenario: Five consecutive source snapshots preserve exact changes
    Given a first source snapshot with main and feature Git refs
    When that source snapshot is staged and review-only prepared
    And a second snapshot changes only main
    And a third complete snapshot removes feature
    And a fourth complete snapshot restores feature
    And a fifth complete snapshot contains no further changes
    Then the source journals show Added, Changed, Missing, Restored and zero delta
    And the restored ref revision is three
    And the zero delta has deterministic empty SHA-256 evidence
    And the first snapshot's prepared evidence never changes

  Scenario: Crash recovery during the second source cycle
    Given the second staged source snapshot was claimed by a worker
    And that worker crashes and the PostgreSQL lease expires
    When two independent replicas race to reclaim the second cycle
    Then exactly one replica receives the next generation
    And the old worker cannot publish failure or a result
    And the new worker prepares only the second snapshot's changed ref
    And LeaseReclaimed and Claimed are both recorded

  Scenario: Staging and enqueue for the same source are serialized
    Given a producer wants to enqueue snapshot A
    And the staging transaction holds the same source advisory lock
    When that staging transaction commits newer snapshot B
    And the queued producer acquires the lock
    Then enqueueing A fails with IncrementalSnapshotSuperseded
    And no stale queued row or immutable audit event is written
    And B may be enqueued after the staging lock is released

  Scenario: Idempotent replay does not create duplicate review changes
    Given the fifth snapshot has already been fully staged and prepared
    When that same source snapshot and queue request are replayed
    Then the preparation ID, source journal and audit count are unchanged
    And a new destination cannot start a stale earlier snapshot
    And no remote GitHub write or MigrationComplete is emitted
