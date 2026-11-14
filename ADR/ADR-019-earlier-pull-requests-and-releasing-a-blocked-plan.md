# ADR-019: see the earlier pull request before planning, and release a blocked plan only after looking again

Status: Accepted (0.9.0). Resolves the two limitations ADR-016 left open. Everything here was tested against the simulators and PostgreSQL; nothing was run against github.com (see *Not verified*).

Context: two situations the destination alone cannot explain.

1. **A file that is not on the base branch yet.** The conversion was published and its pull request is still open. A Triggers proposal derived from it plans against a path that looks free, so it plans
   a *creation* of the same file on another branch. The two pull requests conflict the moment either is merged, and nothing in the plan said so. It is not only the parent: another conversion of the same
   pipeline, or an earlier Triggers proposal, has the same effect.
2. **A plan the ledger keeps blocked after the destination is clean.** One live attempt per plan is the lock (ADR-014). An attempt that failed after a write may have happened, one that never finished (the
   process died), and one that was published whose pull request was then closed unmerged all keep that exact plan blocked for good. A person can clean the destination by hand and the plan stays blocked until
   the base branch moves (a different hash). ADR-016 only described this dead end.

Decision:
- **The plan looks at what this product published earlier for the same file.** `EarlierPullRequestOpen` (Blocker) when a pull request this product opened for the same organization, repository, base branch
  and workflow path is still open; `EarlierPullRequestUnchecked` (Warning) when it could not be read (never assumed closed). The ledger is asked by path (`ListForPathAsync`), at most three distinct pull
  requests are read (the newest attempt of each number), each with the destination's read-only credential, and the plan's own branch is left to `HeadBranchExists`. It is checked only when the file is absent
  or is the prior version, so a taken path costs no extra read. It changes `ready`, never `planSha256`: what a person confirms is the same plan.
- **The plan says when the ledger itself is the obstacle.** `AttemptBlocksPlan` (Blocker) when an attempt of this *exact* plan (same proposal, same hash) blocks it: failed after a possible write, never
  finished, or published with its pull request gone. It is added only when nothing else already explains the block (a branch or an open pull request does, and releasing would be refused), and its words
  separate "still running, wait" from "never finished" and "failed". Without it, publishing would be refused at the last moment as `PublicationPlanNotReady`.
- **A release, not a state change.** `POST /api/workloads/pipelines/proposals/{id}/publications/{publicationId}/release` with `{ destinationConnectionId, note }`. Superadmin only, rate limited, the note
  3 to 500 characters. The service re-reads the destination (GET only, the read-only credential) and releases only if no branch, no open pull request and no merged one remains; for a published attempt its
  own pull request must be *seen* closed and unmerged. Otherwise `409 PublicationLeftoversExist` with the leftovers named (`BranchExists`, `PullRequestOpen`, `PullRequestMerged`,
  `RepositoryNotFound`, `PublishedPullRequestNotClosed`) and nothing is marked. An unreadable destination is `422 DestinationUnreadable`, never a clean one.
- **Never while it could be writing.** An attempt that has not reached a final state is refused (`PublicationStillRunning`) until it has not moved for 15 minutes (the writer gives up after two). Then
  it is first abandoned (`Failed`, `PublicationAbandoned`, writes possible: nothing says it did not write) and then released, in two compare-and-swaps, and only after the destination was read clean.
  A failure before any write is not blocking at all (`PublicationAlreadyFree`); a second release is `PublicationAlreadyReleased`; a lost race is `PublicationLedgerConflict`.
- **The database allows exactly one transition on a final attempt.** Five columns (`released_at_utc`, `released_by_id`, `released_by`, `release_note`, `release_evidence`) go from empty to filled, once, with
  every other column exactly as it was (`ck_workflow_publications_release`: all or none, only `Failed`/`Published`, a note of 3 or more characters; the guard trigger refuses anything else, a
  release of an unfinished attempt, and any change to a released one). The live unique index becomes `WHERE NOT (released_at_utc IS NOT NULL OR (state = 'Failed' AND writes_possible = false))`; an index built
  by 0.8 is rebuilt at startup (a predicate that excludes more rows cannot fail on existing data), and the whole DDL is idempotent. The attempt stays on record as it was: a release is a mark, not an edit.
- **Evidence, not text.** What the destination showed (read time, the ledger state, base tip, the pull requests with number/state/merged, whether the branch exists) is stored with the release as JSON. It
  holds no workflow text and no credential.
- **The screen offers it only where the product said so.** After "Check at the destination", a superadmin sees the form only when the reconciliation reports `LedgerStillBlocksPlan` (nothing of the attempt
  is left at the destination and the ledger still blocks): one rule, the server's. A refusal names what is left and reads the destination again; a release clears the plan shown before it, which said the
  attempt was in the way.

Consequences:
- Replanning after a release gives the same hash and the same plan; the released attempt stays beside the new one in the history.
- The release is a decision by a person on a read the product made at that moment; between the read and the next publish something can change, which the publish re-checks like any other plan (ADR-014).
- Releasing is not retrying: the product never publishes on its own, and a release writes nothing at the destination.
- Two limits remain by design. A plan is blocked by an earlier open pull request only when this product opened it (a person's pull request for the same file is invisible until it is merged and
  the path is occupied). And the 15-minute rule is a time, not proof: an attempt that is still writing after 15 minutes would be found by the destination read (its branch exists) and refused.
- **Not verified online** (nothing in this record was run on github.com): `GET /repos/{owner}/{repo}/pulls/{number}` as the single-pull-request read the plan uses, and how quickly a branch a person
  deleted disappears from the read (the simulator is immediate; the product refuses while the branch is visible, so a delay would only mean "check again"). The statuses of GitHub's refusals are as in ADR-014/016.

Alternatives considered: letting a superadmin release without a read (rejected: the whole point is that the product looked, and a person's "I cleaned it" is a claim); resetting the row's state to something
free (rejected: the ledger is an audit record that moves only forward and an attempt must stay as it was); a release table beside the ledger (rejected: the unique index would still have to ignore the attempt, and
two places would then say whether a plan is blocked); releasing automatically after a verification job (rejected: the product does not decide on a person's behalf what is safe to repeat); blocking a plan on any
open pull request for the same path (rejected: it would need the destination to list every pull request in the repository, and the product only ever answers for what it published).
