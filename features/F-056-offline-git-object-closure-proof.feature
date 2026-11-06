Feature: F-056 independently verify all selected local Git reachable objects
  As an operator checking a source bundle and an offline sandbox target
  I want Git-object integrity evidence rather than only matching refs
  Without interpreting the local proof as remote GitHub success

  Scenario: SHA-1 branch merge tree blob and annotated tag closure match
    Given two trusted separate local bare repositories
    And an exact SHA-bound source branch and annotated tag set
    And the target retains unrelated target-only refs
    When the offline object auditor runs full fsck and enumerates reachable OIDs
    Then source and destination sorted object sets are identical
    And target-only refs remain untouched
    And the receipt never authorizes a remote push

  Scenario: SHA-256 object closure is supported
    Given matching SHA-256 object format on both bare repositories
    When the offline auditor compares every reachable OID
    Then a SHA-bound non-executable local proof is produced

  Scenario: A corrupted pack or missing object is encountered
    Given a local target with an altered Git pack
    When full Git fsck validates the target
    Then no success receipt is issued

  Scenario: A ref changes without changing repository object content
    Given the original exact source and target ref observations
    When another actor changes a target ref before verification
    Then verification fails closed with ref drift

  Scenario: External or partial object storage is forbidden
    Given a shallow, promisor, graft, alternate or symlinked repository
    When the auditor is requested
    Then verification is refused without executing transport

  Scenario: Large repository resources exceed local bounds
    Given more reachable Git objects than the bounded proof accepts
    When a local audit is attempted
    Then it fails closed and does not issue a false receipt
