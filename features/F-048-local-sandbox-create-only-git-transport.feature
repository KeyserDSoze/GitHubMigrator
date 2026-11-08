Feature: F-048 real Git create-only sandbox transport
  As a migration engine operator
  I need to verify physical Git object transfer and ref fencing
  Without granting a write capability to GitHub

  Scenario: The exact missing branch and annotated tag are created
    Given a SHA-verified source Git bundle and a persistent trusted local bare target
    And matching refs and target-only refs already exist in the target
    When the isolated F-048 runner executes atomic create-only push
    Then the missing branch and annotated tag are created with exact object IDs
    And all pre-existing target refs are untouched
    And Git fsck and independent post-write ref verification succeed
    And a SHA-256 sealed local-only receipt is produced

  Scenario: A different actor creates one of the intended refs first
    Given the F-048 packet expects an absent feature branch
    When that branch appears in the target before the worker begins
    Then F-048 rejects SandboxBaselineChanged
    And never force-overwrites a target ref

  Scenario: The acknowledgement is lost after Git already accepted the push
    Given a real atomic local push successfully created the refs
    When the transport acknowledgement is lost
    Then the worker reports SandboxOutcomeRequiresReconciliation
    And a separate Inspect operation observes the exact new refs
    And automatic retry remains prohibited

  Scenario: Two workers use the same sandbox destination
    Given the same F-048 packet and destination
    When two workers attempt a transfer at once
    Then the cross-process target lock serializes their work
    And exactly one creates the missing refs
    And the second rejects the changed baseline
