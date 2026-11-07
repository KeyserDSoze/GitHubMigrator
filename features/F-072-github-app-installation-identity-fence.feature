Feature: F-072 GitHub App installation organization identity before token mint
  A reported scoped GitHub App token is not enough if the numeric
  installation ID belongs to another account or is suspended.

  Scenario: Verified organization installation is allowed to mint a narrow token
    Given an App JWT and a configured installation ID
    When GET /app/installations/{installationId} confirms the App ID
    And account login, organization type and target account ID all agree
    And the installation is active and reports contents write permission
    Then POST access_tokens requests exactly one target repository
    And only contents write and implicit metadata read are accepted
    And the reader may continue with fixed-host repository GETs
    And no remote GitHub write is issued or authorized

  Scenario Outline: Untrusted installation stops before credential issuance
    Given a GitHub App installation response with <mutation>
    When the F-043 inspector validates that installation response
    Then the reader fails closed with a sanitized error code
    And no installation token is minted
    And no repository GET or remote write is performed

    Examples:
      | mutation |
      | wrong installation id |
      | wrong App id |
      | wrong organization login |
      | account type User instead of Organization |
      | mismatched target account id |
      | mismatched target account type |
      | suspended installation |
      | contents read rather than contents write |
      | duplicate login JSON fields |
      | duplicate app id JSON fields |
      | invalid repository selection |
      | malformed JSON |
      | HTTP 404 |

  Scenario: F-051 remote preflight remains read-only and write-disabled
    Given a verified active App installation and F-050 write-disabled fence
    When the App-scoped preflight mints its one-repo token
    Then the only non-GET request is GitHub installation token issuance
    And fixed-host repository, refs and policies are inspected with GET
    And executionAuthorized, remoteWritesPerformed and readyForExecution
      are all false
