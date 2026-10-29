Feature: Review guest users and external collaborations
  Scenario: Identity outside destination organization
    Given a discovered source identity is a guest or external user
    When the operator previews access mappings
    Then guest membership and invite requirements are presented separately
    And no invitation or permission is granted without approval
