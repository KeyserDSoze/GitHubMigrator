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

  Scenario: Graph user pages are checkpointed and observed transactionally
    Given a leased Azure DevOps discovery worker reading multiple Graph user pages
    When each sanitized page is committed into PostgreSQL
    Then only allowlisted identity metadata is stored as SHA-256 observations
    And saved Graph pages can be replayed without duplicate audit events
    And conflicting descriptors roll back the entire new page and its batches

  Scenario: Identity source evidence must match the immutable snapshot before staging
    Given an immutable source snapshot and Graph user observations bound to its job
    When the worker reconciles the source snapshot and the user observations
    Then a missing or changed descriptor, name, email or legacy descriptor blocks staging
    And no Graph observation is interpreted as verified effective access
    And identity drafts continue to use the complete legacy snapshot until paginated consumers are implemented

  Scenario: Durable Graph users are externalized into PostgreSQL instead of a source identity list
    Given a leased discovery worker reading two Graph user pages
    When the source page is persisted with its normalized identity observations
    Then the source reader does not retain all user records in memory
    And the immutable snapshot has an explicit identity count and an empty inline identity array
    And a partial Graph scope never becomes an authoritative empty user inventory
    And the worker verifies original page metadata before staging

  Scenario: Externalized Graph identities are staged in bounded source-scoped transactions
    Given a successful verified Graph source scan with more than 1000 identities
    When the leased worker commits canonical PostgreSQL staging
    Then it processes no more than 250 normalized identity observations at once
    And all identity assets retain the existing F-013 fingerprint, revision and missing semantics
    And a fingerprint mismatch rolls back the full project, identity and ref transaction
    And legacy manual staging refuses the externalized snapshot instead of treating identities as deleted

  Scenario: Durable Azure DevOps source discovery avoids collecting all Git refs in RAM
    Given a leased discovery worker reading more than one Azure DevOps refs page
    When each page is committed into PostgreSQL with its immutable checkpoint
    Then the source reader retains only its running ref count
    And the immutable source snapshot has an empty inline refs array and an explicit externalized ref count
    And the worker seals a matching ref manifest from the persisted observations
    And legacy manual staging cannot misinterpret externalized refs as absent

  Scenario: Git ref staging consumes manifest chunks without a second full ref array
    Given a completed source inventory with 1250 Git refs and verified persisted chunks
    When the durable source worker normalizes the snapshot into PostgreSQL
    Then the staging projection excludes the large refs JSON array
    And each verified chunk is applied in batches of no more than 250 refs
    And the saved catalog retains the existing F-013 revision and missing semantics
    And a failed chunk or expired lease rolls back the entire staging transaction

  Scenario: Complete source ref manifest can be consumed in bounded verified chunks
    Given an immutable source snapshot and source ref pages saved under an active lease
    When the worker prepares its chunk manifest before committing the staging catalog
    Then it verifies the full repository, ref name and object ID multiset
    And only a successful discovery job can publish the immutable chunk manifest
    And a client can page chunk descriptors and read one integrity-checked chunk at a time
    And a corrupted ref fingerprint fails verification instead of being returned

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
