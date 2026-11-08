Feature: Deterministic per-ref source change fingerprint manifest foundation
  Every immutable F-025 source delta can produce a source- and snapshot-bound
  SHA-256 of the original canonical ref changes without loading the whole
  ref set into memory or exposing authorization to GitHub.

  Scenario: The source-only ref change manifest is deterministic
    Given a committed PostgreSQL F-025 staging journal
    When the F-091 reader streams refs in canonical source-id order
    Then its SHA-256 commits to each ref's identity, kind, before and after fingerprint, and revision
    And exactly the observed Added, Changed, Restored and Missing counts are reported
    And no GitHub token, remote transport or migration completion is reachable

  Scenario: A new source epoch or changed ref changes the digest
    Given two immutable snapshots for the same source connection
    When the second snapshot changes a Git ref's object ID
    Then the second manifest has a different digest
    And reverting or restoring a ref is explicit evidence, not an automatic push

  Scenario: Bad evidence fails closed
    When the stream contains a duplicate key, out-of-order key, noncanonical JSON key or invalid fingerprint
    Then F-091 refuses the entire manifest
    And canceled streams never produce a partial successful result

  Scenario: A foreign source cannot certify another source's snapshot
    Given an F-025 snapshot belongs to source connection A
    When source connection B asks for its F-091 proof
    Then no manifest is produced
    And the transaction does not modify staging or GitHub
