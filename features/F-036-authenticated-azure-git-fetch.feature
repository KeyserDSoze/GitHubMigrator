Feature: F-036 authenticated source-only Azure Git fetching
  As a migration administrator
  I need a verified Git bare mirror fetched from my Azure DevOps Services source
  So that an immutable Git bundle can be created without a manual mirror or any GitHub writes

  Scenario: Source URL and credentials are never supplied by callers
    Given a current F-029/F-032 plan, blocked F-034 workpack and sealed F-014 inventory
    When an administrator requests Azure Git acquisition with the exact reconciliation digest
    Then the Azure organization, project and repository are derived from certified records
    And the Azure PAT is read from the existing encrypted connection secret store
    And the remote Git URI is HTTPS dev.azure.com only
    And no GitHub API or Git push is called

  Scenario: Fetch real Git history and store its immutable artifact proof
    Given a valid Azure Git source and complete branch/tag OID evidence
    When the bounded read-only fetch finishes
    Then all ref names and OIDs are checked against the original F-014 snapshot
    And Git verifies all reachable objects using fsck
    And the F-035 bundle export is persisted in PostgreSQL with SHA-256
    And readyForExecution remains false

  Scenario: Any drift or extra branch aborts acquisition
    Given a branch OID has changed or an extra ref has appeared
    When the Git fetch completes into a temporary bare repository
    Then the attempt is rejected
    And no final mirror or bundle evidence is promoted

  Scenario: Repeated requests do not download again
    Given a previously verified mirror and its matching F-035 bundle checkpoint
    When the administrator repeats the same SHA-bound acquisition
    Then the existing immutable proof is returned without another Azure fetch

  Scenario: Remote endpoints cannot be redirected or injected
    Given untrusted source metadata or a malicious URL
    When a Git command is prepared
    Then only the fixed Azure HTTPS host is accepted
    And HTTP redirects, credential helpers and non-HTTPS Git protocols are disabled
    And the PAT is absent from command-line arguments and file paths

  Scenario: No-write safety remains enforceable
    Given a verified source bundle from a remote fetch
    When any actor tries to enable destination execution or update a GitHub ref
    Then PostgreSQL does not allow an executable workpack
    And no GitHub writer is invoked
