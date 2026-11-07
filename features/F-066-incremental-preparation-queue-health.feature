Feature: F-066 source-scoped PostgreSQL incremental preparation queue health
  Operators need trustworthy, safe visibility into review-only staging
  preparation workloads without triggering migration execution.

  Scenario: Superadmin sees source-scoped queue timing and retries
    Given a source with queued, delayed, running, expired, prepared and failed jobs
    When a superadmin reads that source's queue health endpoint
    Then all state counters use one PostgreSQL observation timestamp
    And total jobs equal the sum of disjoint queue state buckets
    And retrying queued jobs are counted within queued jobs only
    And successful review-item throughput for the past day is reported
    And no raw error code, job ID or lease token is returned

  Scenario: Terminal errors are grouped without exposing untrusted text
    Given terminal source-integrity, lease-timeout and unknown errors
    When queue health is queried
    Then failed errors are categorized only as source, lease or other
    And the failure category counts add up to terminal failed runs
    And these failures never authorize automatic retries or GitHub pushes

  Scenario: Empty source is reported without fabricated latency
    Given a valid but unknown source GUID and enabled diagnostics
    When queue health is queried
    Then total counts are zero
    And waiting-time and turnaround-time metrics are null
    And attentionRequired is false

  Scenario: Only reviewed operators may access health
    Given the queue health endpoint
    When an anonymous caller, user, ordinary admin or unrotated admin calls it
    Then the request is denied
    And the feature-off setting returns not-found even to superadmins

  Scenario: Diagnostics cannot execute a migration
    Given expired worker leases and terminal preparation failures
    When a superadmin checks queue health
    Then no row, lease or audit event is modified
    And no external provider endpoint is contacted
    And no GitHub destination write or migration-complete flag is produced
