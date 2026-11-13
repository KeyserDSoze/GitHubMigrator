Feature: F-073 final read-only GitHub ref and repository identity fence
  A remote preflight cannot prove a stable destination using GitHub refs
  observed before a potentially lengthy candidate-policy review.

  Scenario: Stable ref set and repository identity produce read-only evidence
    Given an active GitHub App installation for the intended organization
    And a one-repository scoped ephemeral token was minted
    When heads and annotated tag object refs are read
    And candidate protection policies are inspected
    And heads and tags are read a second time with the same token
    And the numeric repository ID and owning organization are reverified last
    Then only the final typed ref observation may be sealed
    And no remote write or migration-complete action is authorized

  Scenario Outline: Changed Git ref during policy inspection is refused
    Given the initial GitHub ref observation is stable
    When the <kind> object ID changes after the first read
    And the policy inspection finishes
    Then the second complete ref readback detects the change
    And no successful F-051 preflight receipt is produced
    And no repository write is attempted

    Examples:
      | kind |
      | branch |
      | annotated tag |

  Scenario Outline: Late destination mutation refuses the preflight
    Given both heads/tags GET passes report identical ref sets
    When the final repository metadata GET reveals <mutation>
    Then repository identity attestation fails closed
    And no successful F-051 preflight receipt is produced
    And the scoped token remains undisclosed
    And GitHub destination writes remain disabled

    Examples:
      | mutation |
      | numeric repository id replaced |
      | owning organization changed |
      | repository archived |
      | repository disabled |
      | duplicate JSON identity field |
      | hidden repository returned 404 |

  Scenario: An optimistic reread is not permission to execute a GitHub push
    Given a complete consistent remote preflight
    When its read-only F-051 receipt is returned
    Then transport authorization is false
    And remote writes performed is false
    And ready for execution is false
    And a future real sender must independently fence the current target
