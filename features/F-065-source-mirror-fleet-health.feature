Feature: F-065 safe read-only lifecycle fleet diagnostics
  Superadmins need aggregate visibility into durable local source Git
  mirror worker leases without exposing secrets or enabling execution.

  Scenario: An authorized superadmin inspects source mirror health
    Given the local source mirror lifecycle feature is enabled
    And PostgreSQL contains active, expired, verified and quarantined mirrors
    When a superadmin reads the operations source-mirror health endpoint
    Then the response includes PostgreSQL-clock aggregate counts
    And expiring-soon leases are a subset of active leases
    And total mirrors equal all state partition counts
    And the response contains no worker IDs, lease tokens or secrets
    And the endpoint does not modify any source or destination

  Scenario: Feature flag remains fail-closed
    Given the local source mirror lifecycle feature is disabled
    When a superadmin tries to inspect fleet health
    Then the endpoint returns 404 with no lifecycle metadata
    And no GitHub API request is made

  Scenario: Caller authorization is enforced before reading data
    Given the operations health endpoint
    When an anonymous, ordinary user, or admin requests fleet health
    Then the request is rejected
    And an unchanged bootstrap password does not authorize inspection

  Scenario: An expired owner is flagged but never automatically retried
    Given a source-mirror lease has expired
    When the fleet reader observes PostgreSQL lifecycle state
    Then expiredLeases is greater than zero
    And attentionRequired is true
    And no lease is acquired or automatically quarantined
    And no migration is marked complete
