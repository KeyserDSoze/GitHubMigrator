Feature: Convert pipelines to reviewed GitHub Actions
  Scenario: Assist with conversion
    Given a supported Azure DevOps pipeline and an approved AI provider configuration
    When the operator requests a suggested GitHub Actions workflow
    Then secrets, service connections and variable groups are redacted before the AI call
    And the proposed workflow and unsupported tasks are shown as a diff
    And no workflow is pushed without human approval

  Scenario: AI unavailable
    Given Foundry has no deployment or quota
    When a conversion is requested
    Then the feature offers a deterministic/manual fallback
    And the system does not silently claim equivalence

  Scenario: A model connection is created only after the model answered
    Given an operator adds an AI model connection with an allowed endpoint and an API key
    When the product asks the model a fixed test question
    Then the connection is saved only if the model answered
    And the key is stored encrypted and never returned by the API
    And an endpoint outside the operator allow-list is refused before any request leaves

  Scenario: Pipeline source goes to a model only with consent
    Given a verified AI connection
    When nobody with the superadmin role has allowed sending pipeline source
    Then no pipeline source is sent to that model
    And allowing it is recorded with who and when

  Scenario: A model's answer is data, not an approval
    Given a model returned a workflow
    When the gates run
    Then pinned and allow-listed actions, manual-only triggers, least-privilege permissions and plan-only secrets are enforced
    And actionlint and zizmor must have run, otherwise the workflow is not approvable
    And a linter-suppression comment written by the model does not silence either linter

  Scenario: A conversion is a proposal with its evidence
    Given a verified AI connection with source sending allowed
    When the operator asks for a conversion of a pipeline
    Then the source is redacted before it is sent and every credential is masked wherever it appears
    And the answer is a proposal that is neither saved, approved nor published
    And it comes with the gate results, the secrets and variables it may use, and the provenance of the question

  Scenario: No step is dropped silently
    Given a pipeline that uses Azure tasks
    When the model returns a workflow
    Then every task is declared converted or marked MANUAL PORT REQUIRED, otherwise the workflow is blocked

  Scenario: A kept proposal is approved by a second person against its hash
    Given a conversion proposal that passed every required gate
    When a person other than its author approves it, sending the SHA-256 of the text they read and accepting each warning by name
    Then the gates are run again on the kept text before anything is recorded
    And the decision records who, when, the hash, the fresh report hash and the warnings accepted
    And approving publishes nothing and does not re-enable the original triggers

  Scenario: An approval cannot be recorded for a text that changed or a verdict that no longer holds
    Given a kept proposal
    When the hash sent differs, or the stored text no longer matches its hash, or the gates now block it
    Then no approval is recorded and the proposal stays undecided

  Scenario: A blocked answer is kept as evidence without its text
    Given a model answer that the gates block
    Then it is kept with its findings and provenance but without the workflow text
    And it cannot be approved

  Scenario: An approved workflow is planned against the destination without writing anything
    Given an approved proposal and a verified GitHub destination connection
    When the operator asks for a publication plan for a repository
    Then only GET requests are made, only inside that repository
    And the plan names the branch, the file, the pull request title and description and the commands a person runs
    And it reports a different file at the path, a taken branch, an open pull request, an archived or missing repository as findings
    And its hash covers the base commit, so a plan made after the base moved has another hash

  Scenario: Nothing is planned for a proposal nobody approved
    Given a proposal that is pending, rejected, revoked or blocked
    When a publication plan is requested
    Then it is refused and the destination is not contacted

  Scenario: Publishing is off unless the operator enabled it and allow-listed the repository
    Given a deployment with no publication configuration
    When a superadmin asks to publish an approved plan
    Then the answer is that publishing is disabled and nothing is read from or written to the destination

  Scenario: Only the confirmed plan is published, once
    Given an approved proposal, an enabled publisher and a plan that is ready
    When the superadmin confirms the plan by its hash and publishes
    Then the plan is made again and must have the same hash, and the attempt is recorded before any write
    And the product creates the branch, commits the one file and opens the pull request, then reads them back, and writes nothing else
    And nothing is merged and the same plan cannot be published a second time

  Scenario: A write whose outcome is unknown is never repeated
    Given a publication whose file write fails after the branch was created
    Then the ledger records that something may exist at the destination and the product does not retry
    And a plan for that repository now reports the existing branch and cannot be published over

  Scenario: A refusal before any write frees the plan
    Given a publication refused because the installation lacks a permission
    Then nothing was written and the same plan can be published after the operator fixes the installation
