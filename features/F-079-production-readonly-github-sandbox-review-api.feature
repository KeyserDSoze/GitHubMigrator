Feature: Admin-only, write-disabled live GitHub sandbox review API
  The existing trusted F-076/F-077/F-078 review must be callable from
  the running service without exposing a GitHub sender, raw credentials,
  refspecs or any remote execution permission.

  Scenario: A fresh PostgreSQL database can initialize sandbox session history
    Given the F-078 session table references the F-055 transmit hazard table
    When the API starts from an uninitialized PostgreSQL database
    Then it initializes F-055 before F-078
    And no missing foreign-key table prevents startup

  Scenario: An authenticated independent superadmin records a review
    Given one repository is explicitly allowlisted for test-only reviews
    And the F-055 hazard has a reviewed F-052 grant and immutable F-077 preflight
    When the superadmin submits only the hazard ID to the scoped session endpoint
    Then the server rebuilds F-044 and authenticates fresh GET-only target reads
    And F-075 checks the control plane under PostgreSQL repository locks
    And F-078 stores one immutable, write-disabled session
    And no remote GitHub mutation or ref creation is performed

  Scenario: Untrusted and disabled requests cannot perform a review
    Given sandbox review is disabled by default
    When an anonymous caller invokes the session endpoint
    Then access is denied
    When a non-superadmin user invokes the session endpoint
    Then access is forbidden
    When an authenticated superadmin invokes the disabled endpoint
    Then the response refuses with SandboxReviewSessionDisabled
    And no private key or provider call occurs

  Scenario: Historical session inspection cannot leak cross-scope sessions
    Given an immutable F-078 session belongs to an exact preparation, plan and scope
    When an authorized admin requests it using a different scope
    Then the receipt is not returned
    When the authorized admin requests it using the correct scope
    Then the immutable audit and current database authority are rechecked
    And the response cannot authorize writes, retries or completion

  Scenario: A possible-send hazard is never reactivated
    Given an F-055 hazard has been marked MayHaveTransmitted
    When a superadmin re-requests its sandbox session
    Then the trusted review refuses any new write-disabled session
    And an existing session is historical only
    And the API has no retry or remote-send action
