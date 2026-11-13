Feature: Independent read-only GitHub recovery after a possible send
  Reconstruct the original pre-send refs from F-086 after a crash.
  Observe remote objects; never dispatch another push or infer completion.

  Scenario: F-082 is required before any recovery provider reads
    Given an F-086 typed baseline exists but F-082 was not committed
    When an independent operator asks to observe remote tips
    Then the request is rejected before reading credentials or GitHub
    And no sender, retry or remote mutation is possible

  Scenario: Durable read-only F-054 observation after grant expiry
    Given the original F-052 and F-050 deadlines have expired
    And F-082 MayHaveTransmitted was committed before a worker crash
    When a separate observer invokes recovery observe-tips
    Then a scoped Contents:read token is minted only inside the callback
    And the original F-086 typed heads, tags and missing refs are compared
    And one immutable F-054 receipt and audit are committed in PostgreSQL
    And F-085 reports TipsObservedNeedsClosure after a restart
    And all retry, dispatch and migration-complete flags remain false

  Scenario: Complete object proof is a separate independent stage
    Given an exact F-054 observation was persisted
    When a different observer invokes verify-full-objects
    Then trusted F-059 revalidates the source plan, snapshot and bundle
    And GitHub refs are checked and fetched via smart HTTPS upload-pack
    And F-056 audits the complete reachable Git object graph
    And only a matching F-058 receipt is persisted
    And the migration is not marked complete

  Scenario: Unexpected remote branch or missing candidate requires review
    Given F-082 was marked as possibly transmitting
    When GET-only remote readback shows a divergent target ref
    Then F-054 reports OperatorReviewRequired
    And no full-closure fetch, retry or sender is invoked

  Scenario: No duplicate remote observations on repeated manual calls
    Given an immutable F-054 observation is already stored
    When another request asks to observe the same epoch
    Then the stored hash-only receipt is returned without minting a token
    And a conflicting observation cannot overwrite it

  Scenario: Recovery HTTP endpoints are disabled by default
    Given no operator has configured a recovery allowlist
    When a superadmin calls either body-free recovery action
    Then no provider call is made
    When a normal user attempts the same action
    Then access is denied
