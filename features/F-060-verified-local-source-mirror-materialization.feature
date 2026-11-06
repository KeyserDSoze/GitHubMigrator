Feature: F-060 verify and materialize frozen F-038 source mirrors
  As a migration operator verifying a source history
  I want the Git source mirror to be physically and cryptographically
  bound to the original reviewed source snapshot and immutable bundle
  Without allowing any GitHub push or blind retry

  Scenario: Two workpacks in a snapshot cannot collide on disk
    Given one F-029 snapshot and two distinct source repository workpacks
    When the local physical mirror IDs are calculated
    Then each workpack has a unique UUID-named bare directory

  Scenario: A current SHA-1 or SHA-256 Git bundle is fully imported
    Given a validated F-038 bundle with complete source Git refs
    When the internal F-060 materializer imports it into a private scratch
    Then real Git fsck and a bounded complete object-set comparison pass
    And the source snapshot Git directory is promoted atomically
    And a F-057 independent read-only fetch can verify the full history

  Scenario: Six replicas import the same source after a possible crash
    Given six worker instances competing for the same snapshot and workpack
    When all attempt import on one shared lock-capable volume
    Then one final physical mirror is published
    And all workers receive identical verification fingerprints
    And no partial staging directory is mistaken for a committed snapshot

  Scenario: A stored bundle or previously promoted mirror is modified
    Given a persisted F-038 source bundle and a physical Git mirror
    When the bundle bytes or Git pack are corrupted
    Then the materializer refuses both creation and reuse
    And it never overwrites or silently repairs an existing final mirror

  Scenario: Remote proof cannot use an unverified source mirror
    Given no current F-038 workpack evidence or no F-029 mapping
    When F-059 begins an independent GitHub read
    Then it rejects the source before provider I/O
    And F-055 is not released and no automatic retry is scheduled
