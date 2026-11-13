Feature: Immutable typed original GitHub baseline for crash-safe independent recovery
  F-059 requires the original branch/tag types and create candidates.
  A SHA-only pre-send journal cannot reconstruct them after a crash.

  Scenario: Capture the last authenticated typed snapshot before any Git send
    Given F-081 is fresh and independently confirmed
    And F-055 is PreparedWriteDisabled and F-083 has completed two GETs
    When the operator requests the internal F-083 one-shot sender
    Then F-086 captures the original typed GitHub refs and missing create candidates
    And the snapshot, actor, plan, scope and SHA are append-only in PostgreSQL
    And only after capture can F-082 record MayHaveTransmitted
    And the snapshot never authorizes dispatch, retry or migration completion

  Scenario: Failure to capture F-086 prevents even a possible send
    Given F-086 is disabled or the typed observation has changed
    When F-083 reaches the last pre-send boundary
    Then F-082 is not consumed by F-083
    And no Git push is invoked

  Scenario: Restart reconstructs the original annotated tags
    Given F-086 was committed before F-082
    And F-082 has since committed MayHaveTransmitted
    When a fresh database connection reads the same plan and scope
    Then the SHA-sealed names, OIDs and original commit/tag types match
    And no operator supplies ref names, OIDs, URL or credentials

  Scenario: Capture without a possible-send marker is not proof of a send
    Given F-086 captured an independent typed baseline
    And F-082 was never committed
    When independent recovery reads the historical epoch
    Then no F-086 replayable post-send snapshot is returned

  Scenario: Six replicas cannot replace a captured baseline
    Given six PostgreSQL connections race to capture one confirmation
    When they acquire the ordered repository locks
    Then one immutable snapshot and one event are committed
    And no second capture overwrites the original observation

  Scenario: Altering a tag type, object ID or a no-write flag is rejected
    Given a persisted F-086 snapshot for a single approved sandbox
    When SQL updates the type, OID, digest, audit event or execution flags
    Then PostgreSQL rejects the mutation
    And independent SHA-256 verification detects a forged in-memory record

  Scenario: HTTP exposes only status and is disabled by default
    Given no sandbox has explicitly enabled F-086
    When an anonymous user or non-superadmin reads the typed snapshot status
    Then access is denied
    When a superadmin reads while disabled
    Then the API refuses without any GitHub provider I/O
