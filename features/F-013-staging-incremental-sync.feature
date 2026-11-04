Feature: F-013 intermediate staging and incremental synchronization

  Scenario: Repeating the same source snapshot is idempotent
    Given an immutable discovery snapshot
    When it is applied to the PostgreSQL staging catalog twice
    Then the sync ledger and asset revisions are unchanged

  Scenario: Partial source coverage is not deletion evidence
    Given a prior snapshot with a Git branch
    When a later partial scan does not show that branch
    Then staging keeps the previous branch as last observed
    And does not mark it as deleted

  Scenario: Changed Git refs increment only their own revision
    Given a staged repository with a commit SHA
    When a newer snapshot has a different SHA for the same ref
    Then the ref fingerprint changes and its revision increments
    And the repository name revision remains stable

  Scenario: Stale scans cannot regress state
    Given a newer synchronized source snapshot
    When an older unsynced snapshot is applied
    Then HTTP 409 is returned and the catalog remains unchanged

  Scenario: Sync failures cannot leave half-applied batches
    Given a PostgreSQL batch that fails after an earlier batch
    Then the transaction rolls back all asset rows and ledger changes
    And retrying a corrected batch succeeds

  Scenario: Worker-friendly catalog reads are bounded
    Given thousands of normalized repository refs
    When a client reads the staging catalog using the keyset cursor
    Then at most 100 records are returned per page
    And repeated pages do not return duplicate source IDs
