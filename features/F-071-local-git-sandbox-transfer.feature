Feature: F-071 real full-history Git transfer to a local-only sandbox
  Verify the physical Git transport path before enabling ANY real
  GitHub destination write or execution authorization.

  Scenario Outline: Branches, merge ancestry and tags are transported
    Given a F-038 verified Git bundle with <format> objects
    And a F-060 sealed local source mirror with main, feature and v1 tag
    When the internal F-071 runner transfers into a fresh local bare sandbox
    Then every source branch and annotated tag has the identical Git OID
    And the source and target fully pass F-056 fsck closure verification
    And reachable commit, tree and blob object digests are identical
    And no remote push, GitHub credential or migration completion is emitted
    Examples:
      | format |
      | sha1 |
      | sha256 |

  Scenario: Six concurrent workers cannot create duplicate sandboxes
    Given six sandbox transfer requests for the same physical source and GUID
    When the requests execute concurrently
    Then a single target bare directory is created
    And every worker returns the same content-bound evidence digest
    And no duplicate, overwrite or source ref mutation is performed

  Scenario: A changed existing target is never overwritten
    Given a successfully verified local sandbox destination
    When its main ref is altered before a repeat transfer
    Then the runner rejects the stale or conflicting target
    And the existing target ref is left untouched
    And no force refspec or automatic repair is issued

  Scenario: Untrusted source receipt and physical ref change fail closed
    Given a genuine source mirror
    When an actor forges its source proof or changes the source refs
    Then the transfer is refused before writing a destination
    And the error reveals no local filesystem path or credential

  Scenario: Local-only transport is disabled by default
    Given a production application has no registered sandbox transfer runner
    When a caller tries to use sandbox transfer without explicit test opt-in
    Then transfer is denied
    And no GitHub URL, HTTP request, token, remote push or migration-complete
      action is possible
