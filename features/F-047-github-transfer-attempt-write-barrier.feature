Feature: F-047 durable GitHub transfer preparation and remote write barrier
  As a superadmin preparing a bounded sandbox migration
  I need durable independent authorization intent and unambiguous audit
  Without allowing a preparatory review to mutate GitHub

  Scenario: Two replicas cannot create two active target attempts
    Given current F-044 and F-046 hashes and one held F-045 lease
    When multiple replicas prepare the same GitHub repository
    Then exactly one attempt and Prepared event are persisted
    And every competing scope is rejected until the target leaves the active state

  Scenario: A separate human reviews but does not authorize a push
    Given an AwaitingIndependentReview attempt with a live F-045 lease
    When the original actor tries to review
    Then the review is refused with TransferPreparationTwoPersonRequired
    When a different superadmin acknowledges the dry run
    Then the state is Reviewed with execution_authorized false

  Scenario: Worker claims exactly one reviewed attempt and stops
    Given the attempt has an independent dry-run review
    When multiple worker replicas poll PostgreSQL concurrently
    Then only one WriteBarrierBlocked event is appended
    And the state is BlockedAtWriteBarrier
    And transport_attempted and all write/authorization flags remain false

  Scenario: PostgreSQL rejects remote execution flags regardless of role
    Given an F-047 attempt has been persisted
    When direct SQL tries to enable transport_attempted, execution_authorized or remote_writes_performed
    Then PostgreSQL rejects the update
    And no GitHub ref is created or updated
