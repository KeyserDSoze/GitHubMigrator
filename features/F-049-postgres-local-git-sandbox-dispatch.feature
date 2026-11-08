Feature: F-049 durable local Git sandbox dispatch with crash reconciliation
  As a GitHub migration engine operator
  I need a PostgreSQL-backed local Git sandbox worker
  Without permitting a GitHub remote write or a blind retry

  Scenario: Exact F-044 refs are queued only after a separate F-047 review
    Given a fresh F-044 packet and F-032 reconciliation
    And a F-047 attempt independently reviewed and blocked at the remote write barrier
    And a server-allowlisted preprovisioned local bare sandbox
    When a superadmin enqueues a local sandbox transport
    Then the full SHA-bound local transfer plan is persisted in PostgreSQL
    And no user-controlled Git URL, path or refspec is accepted

  Scenario: Multi-replica conditional claim cannot send twice
    Given multiple worker replicas polling the same queued local job
    When a worker commits its first potential-send claim
    Then exactly one PotentialSend event is appended
    And no other replica can claim the job for transport

  Scenario: Real Git transfer succeeds from a PostgreSQL dispatch
    Given a SHA-verified source Git bundle and empty trusted bare sandbox
    When the local worker runs an atomic create-only transfer
    Then real Git objects and refs arrive
    And exact target SHA, git fsck and the bundle SHA are verified
    And PostgreSQL records Verified without granting GitHub write authority

  Scenario: Process crashes or loses acknowledgement after a push
    Given the job already entered MayHaveTransmitted
    When it loses its worker before recording the transport outcome
    Then a later worker may claim only the read-only inspection
    And it must not return to Queued or attempt another Git push
    And the independent target inspection records ReconciledMatch or NeedsOperator

  Scenario: SQL attempts to grant remote write permission
    Given any F-049 job including an independently verified one
    When a direct SQL writer tries to set remote_writes_performed to true
    Then PostgreSQL rejects the mutation
