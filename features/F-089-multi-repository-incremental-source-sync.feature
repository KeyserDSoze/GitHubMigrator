Feature: Multi-repository incremental source staging and recovery pulse
  Multiple source repositories can be monitored without authorizing GitHub writes.
  Staging in PostgreSQL is not proof that a source is live-current or GitHub migrated.

  Scenario: One reviewed plan contains two differently changed source repositories
    Given a real immutable F-029 plan with two Azure DevOps Git repositories
    And the F-025 staging journal records ref additions for only the first
    When the superadmin opens source synchronization monitoring
    Then both repositories are visible with exact source IDs
    And each receives its independent added/changed/restored/missing counts
    And the second repository is not marked changed or migrated
    And both plan steps remain blocked and no GitHub push is offered

  Scenario: Exact JSON source repository IDs avoid prefix collisions
    Given the staging ref natural keys use canonical JSON arrays
    When repository "repo-1" and repository "repo-10" have separate refs
    Then PostgreSQL selects only the exact first JSON element
    And no arbitrary ref names, OIDs or provider secrets appear in the API

  Scenario: Newer staging does not silently update a historical plan
    Given a reviewed plan refers to a committed source snapshot
    When a newer source snapshot is committed to PostgreSQL
    Then the old plan is labelled SupersededInPostgres
    And the UI advises re-preparation and review
    And source freshness or GitHub migration completion is never claimed

  Scenario: GitHub recovery stays constrained to one separately allowlisted sandbox
    Given a plan contains multiple target repository names
    And only one target exactly matches the F-088 recovery allowlist
    When the control tower consults possible-send history
    Then only the allowlisted repository can display F-082 recovery evidence
    And unmonitored repositories remain source-staging observations only
    And a forged numeric GitHub repository ID or retry flag is rejected

  Scenario: Source refresh is human-initiated and uses the existing durable queue
    Given a superadmin selects a verified preparation plan
    When the operator explicitly confirms a source refresh
    Then one idempotent Azure DevOps discovery job is requested
    And its durable PostgreSQL status can be checked later
    And no reviewed plan is overwritten or GitHub push requested

  Scenario: Tampered history cannot create a false migration-complete report
    Given the original plan manifest or staging journal is corrupted
    When the superadmin reads the source pulse
    Then the API returns a visible integrity error
    And the frontend refuses any response with push, retry or completion true

  Scenario: Anonymous users and ordinary administrators cannot inspect the pulse
    Given F-089 is disabled by default
    When a non-superadmin requests the pulse
    Then the API denies it without external provider calls
    When a superadmin requests the pulse while disabled
    Then the API returns a specific disabled response
    And there is no POST execution endpoint on the pulse itself
