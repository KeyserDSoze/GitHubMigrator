Feature: Platform authentication and first-login password change
  As the platform operator
  I want to access the system safely
  So that only authorized platform accounts configure migrations

  Scenario: First local test login
    Given a new local development database
    When I sign in as "superadmin" with the development-only password
    Then I receive an authenticated session with mustChangePassword true
    And I cannot create users until the password is changed

  Scenario: Password rotation
    Given an authenticated superadmin with a temporary password
    When I change my password using the correct current password
    Then the previous password is no longer valid
    And mustChangePassword is false

  Scenario: Role permissions
    Given a regular platform user
    When they attempt to create a platform account
    Then the API rejects the operation

  Scenario: Production bootstrap safety
    Given the runtime environment is not Development
    When a default bootstrap password of superadmin is configured
    Then startup fails
