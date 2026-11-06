
Feature: F-037 durable source-only Azure Git acquisition
  Scenario: Only one source acquisition per verified workpack
    Given a current F-029 plan, F-032 reconciliation and blocked F-034 workpack
    When concurrent administrators enqueue the same Azure Git acquisition
    Then the PostgreSQL job and Queued event are idempotent

  Scenario: Recover a mirror checkpoint safely
    Given a worker has fetched and verified the complete source bare mirror
    And it committed MirrorVerified using its unexpired lease
    When its process crashes and another worker reclaims the job
    Then it resumes from MirrorVerified without fetching again
    And the previous lease cannot commit bundle evidence

  Scenario: Completed never means GitHub migrated
    Given an F-035 bundle was saved with exact source and target proofs
    When the worker finalizes its PostgreSQL source job
    Then the phase is BundleVerified and the job state is Completed
    But ready_for_execution remains false in the database
    And no GitHub repository or ref is modified

  Scenario: Queue disabled unless explicitly enabled
    Given AzureGitQueue:Enabled is false
    When an administrator attempts to queue a fetch
    Then the endpoint returns a safe conflict without starting Git
