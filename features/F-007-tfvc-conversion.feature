Feature: Convert TFVC source history with a fidelity report
  Scenario: Changesets as Git history
    Given a supported source TFVC collection and a compatible conversion worker
    When FullHistoryBestEffort runs
    Then every imported changeset records its Git commit mapping, author and original timestamp
    And every unsupported merge, branch, rename or label is explicitly reported

  Scenario: Unsupported history topology
    Given a TFVC branch or merge that cannot be faithfully converted
    When preflight executes
    Then the operator is warned of the loss and may select ArchiveAndTip
    And the system never claims 100 percent fidelity without evidence
