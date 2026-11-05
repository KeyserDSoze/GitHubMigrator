Feature: Reviewable identity mapping drafts (F-004)

  Background:
    Given I am a platform administrator with a changed password
    And an immutable Azure DevOps discovery snapshot exists
    And a verified GitHub Enterprise Cloud destination connection exists

  Scenario: Map a source user without granting GitHub access
    When I map a known source Graph descriptor to a valid GitHub login
    And I save the mapping as a draft
    Then it is scoped to this snapshot and destination connection
    And the saved draft has a revision and updated timestamp
    And preview displays mapped and unmapped identities
    And no GitHub repository permission or invitation is created

  Scenario: Unknown source identity
    When I submit a source descriptor absent from the snapshot
    Then the service rejects the draft
    And the existing draft remains unchanged

  Scenario: Duplicate target login and malformed login
    When I map two source identities to the same GitHub login (ignoring case)
    Or I submit an email, URL or invalid GitHub login
    Then the draft is rejected
    And no mappings are persisted

  Scenario: Concurrent administrators edit one draft
    Given administrator A reads revision 1
    And administrator B saves revision 2
    When administrator A attempts to save revision 1
    Then the API returns HTTP 409 RevisionConflict
    And revision 2 is preserved

  Scenario: Snapshot history is immutable
    Given a mapping saved against snapshot A
    When discovery creates snapshot B
    Then its mappings start empty
    And snapshot A mapping remains available

  Scenario: Incomplete source permissions
    Given source ACLs and group evidence are incomplete
    When I inspect a mapping preview
    Then EffectivePermissionsVerified and ReadyForGrants are false
    And no unmapped user is claimed to have orphaned repository access

  Scenario: Unverified GitHub organization membership
    Given all visible source users have a GitHub login mapping
    When I inspect the preview
    Then TargetAccountsVerified and ReadyForGrants are false
    And the review clearly says GitHub accounts/membership have not been verified

  Scenario: Role protection
    Given a regular platform user
    When they attempt to read or save a mapping draft
    Then the API forbids the operation

  Scenario: Thousands of source identities are reviewed via bounded pages
    Given an immutable snapshot with 1205 Graph users and verified PostgreSQL observations
    When an administrator reads F-004 preview and browses source users
    Then identities are fetched in pages of at most 100, bound to the snapshot ID
    And total mapped, unmapped and unresolved ACL counts are correct
    And Graph source evidence never implies verified GitHub membership or grants

  Scenario: Saving a draft after changing pages preserves earlier mappings
    Given an administrator maps one identity on the first source page
    And maps another identity on a later page
    When the administrator saves the current draft
    Then both mappings are submitted with the same revision
    And earlier page mappings are not silently removed

  Scenario: Corrupt completed-source observations are rejected
    Given a completed discovery job with a source identity snapshot
    When observed descriptors or source metadata diverge even with equal row counts
    Then F-004 refuses the source with IdentitySourceMismatch
    And no draft is silently approved or source user treated as missing

  Scenario: Pre-F-015 legacy snapshots remain reviewable
    Given an immutable discovery snapshot with inline identities and no Graph observations
    When an administrator requests a bounded source user page
    Then PostgreSQL reads only that page from the inline JSONB source
    And it does not substitute identities from a newer unrelated snapshot
