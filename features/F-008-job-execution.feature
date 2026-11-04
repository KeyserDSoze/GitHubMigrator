Feature: Durable and verifiable migration execution

  # First implemented slice: immutable source snapshot -> PostgreSQL staging only.
  Scenario: Concurrent administrators request the same staging job
    Given an immutable source discovery snapshot
    When multiple administrators queue its staging normalization
    Then only one job and one queued audit record exist

  Scenario: Multiple workers cannot own the same attempt
    Given a ready PostgreSQL staging job
    When separate workers try to claim it concurrently
    Then a single lease token and attempt owner are chosen
    And a stale worker cannot mark another worker's attempt complete

  Scenario: Recover a crashed worker
    Given an in-progress staging job whose lease expired
    When a new worker claims it
    Then the attempt counter increases and the old lease is fenced
    And the canonical snapshot is synchronized without duplicate asset revisions

  Scenario: Exhaust bounded retries
    Given staging repeatedly fails without corrupting its source snapshot
    When the fifth bounded attempt fails
    Then the job is marked Failed and has a safe error code
    And its audit events show the attempts and retries

  Scenario: Stage-only safety boundary
    Given no F-005 migration plan has been approved
    When a staging job completes
    Then only the intermediate PostgreSQL catalog changes
    And the job never publishes repositories, Actions or Issues to GitHub

  # Future phases begin after governed F-005 plan approval, not in this slice.
  Scenario: Repository enumeration resumes without re-downloading project metadata
    Given a worker has checkpointed an Azure DevOps project and its capabilities
    When the repository enumeration GET fails transiently
    Then the worker retries without re-fetching the earlier project and capabilities
    And the repository list is persisted using only approved ID and name metadata

  Scenario: Security evidence page recovery retains raw ACL masks only
    Given a leased worker has checkpointed Graph group and membership evidence
    When an ACL GET fails transiently
    Then the source scan is retried and reuses saved group and membership pages
    And raw ACL allow and deny masks are preserved without unrecognized fields
    And effective GitHub permissions remain unverified

  Scenario: Ref page and normalized observations commit atomically
    Given an active Azure DevOps discovery worker and one page of Git refs
    When the source page is saved in PostgreSQL
    Then its normalized SHA-256 ref observations are saved in batches
    And an interrupted page cannot leave a partial ref catalog
    And provisional observations never mark canonical refs as missing

  Scenario: Conflicting source refs reject an entire page
    Given a saved Git ref from the first source page
    When another page contains that same ref name with a different object ID
    Then the new page and all its ref observations roll back
    And the first committed source page and its audit remain unchanged

  Scenario: Failed Azure DevOps ref pagination resumes from the saved page
    Given a leased discovery job which saved the first page of Git refs
    When the second page fails with a transient provider error
    Then the worker retries after bounded backoff
    And the first page and its opaque cursor are reused from PostgreSQL
    And only missing pages are fetched again
    And unrecognized provider JSON fields are never persisted as page checkpoints

  Scenario: Expired worker cannot append discovery pages
    Given an in-progress discovery job that was reclaimed after lease expiration
    When its previous worker tries to save another source page
    Then PostgreSQL rejects that stale checkpoint write
    And the current worker can continue from existing immutable pages

  Scenario: Async Azure DevOps discovery resumes after source snapshot commit
    Given a verified Azure DevOps source connection and an enqueued discovery job
    When a worker saves an immutable snapshot and then crashes before staging
    Then retry loads the snapshot by the same job ID
    And it does not repeat Azure DevOps API reads
    And staging normalization completes and records the result

  Scenario: Only one source discovery can be active for a connection
    Given two administrators request Azure DevOps discovery concurrently
    When their jobs are enqueued for the same source connection
    Then the PostgreSQL queue stores only one active discovery
    And they receive the same job while it remains queued or running
    And another scan can be queued after the first becomes terminal

  Scenario: A checkpoint is fenced by the current worker lease
    Given a previous worker's lease has expired and a newer worker has reclaimed its job
    When the previous worker attempts to save a snapshot checkpoint
    Then the checkpoint update is rejected
    And a stale worker cannot claim the job succeeded

  Scenario: Interrupted migration
    Given a migration was interrupted after a verified push stage
    When a worker resumes with its lease and checkpoint
    Then it does not duplicate completed verified work
    And every stage retains correlation IDs and audit evidence

  Scenario: Incomplete destination
    Given the destination has missing refs or mismatched commit SHAs
    When post-migration verification runs
    Then the job is marked incomplete with actionable errors
    And the operator can export a report without leaking secrets
