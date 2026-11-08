Feature: F-046 GET-only per-ref GitHub rules and ambiguous outcome guard
  As an independently approved GitHub migration reviewer
  I need to inspect effective rules for exact missing refs
  So that a future writer cannot confuse missing rules with permission

  Scenario: A nonexistent branch can have GitHub rules
    Given a fresh F-044 create-only packet whose candidate branch is absent remotely
    When the read-only GitHub effective branch rules endpoint reports active rules
    Then the ref is marked RulesRequireReview
    And the review is not an execution authorization

  Scenario: An inherited tag or push ruleset affects a create candidate
    Given a complete inherited repository rulesets response
    When a tag or push ruleset is active
    Then all affected candidate kinds require manual review
    And no Git command or GitHub write is issued

  Scenario: Ambiguous provider response fails closed
    Given a GitHub rules endpoint with a 403, redirect, duplicate JSON or next page
    When the F-046 review evaluates the candidate
    Then the result is PolicyUnverified
    And no remote write or automated retry is possible

  Scenario: Future Git transport times out after transmission
    Given a possible remote Git push with unknown acceptance
    When the future worker receives a transport timeout or connection reset
    Then the outcome is RequiresRemoteReconciliation
    And blind automatic retry remains forbidden
