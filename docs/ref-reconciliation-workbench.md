# F-033 — Immutable Git ref reconciliation workbench (React)

## Purpose

This increment makes the existing F-029/F-031/F-032 **read-only** evidence
workflow usable in the authenticated React interface. It does not implement
a Git mirror worker or any GitHub mutation.

From the sidebar **Git ref reconciliation / Riconciliazione ref Git**, an
administrator may open an existing incremental preparation by its UUID,
select its F-029 immutable operation plan and repository mapping, capture
new F-031 GitHub branch/tag ref evidence using the existing guarded
provider GET API, create an F-032 source-target comparison from exact
server-provided SHA-256 proofs, and inspect historic comparisons and
bounded pages of ref decisions.

The discovery workbench now provides a direct link to the same view after
an immutable operation plan is created, passing the server-issued preparation
and plan IDs. This avoids manually copying UUIDs in the normal flow.
The manual UUID selector remains useful for revisiting older evidence.

## UI contract and safety

- Strongly typed `api.ts` models match the ASP.NET JSON response for
  F-031 target evidence and F-032 reconciliation. `GitMirrorRefDisposition`
  is serialized as numeric values `0..3`, not arbitrary labels. Each of the
  four dispositions has an Italian and English translation.
- The selected F-029 plan supplies the manifest SHA, and the selected F-031
  observation supplies its immutable evidence SHA and UUID. Neither proof
  can be edited manually. All POSTs go to existing protected backend routes,
  which independently verify proofs and context.
- An expired plan **disables target ref collection and comparison**. An
  expired or superseded target observation **disables comparison**. Historic
  source-target comparisons remain reviewable with a clear `Stale` label.
  The workbench does not offer any authorization/push/delete button.
- Every row displays full Git ref name, shortened object IDs with complete
  IDs available as tooltips, conservative disposition and exact blocker
  reason. Comparison summaries include counts of matching, create candidates,
  divergences and target-only preserved refs.
- The detail table requests **50 refs per page** and follows the server
  `nextAfter` cursor, URL-encoded. No unbounded result fetch or client
  reconstruction of ref lists. UI does not cache tokens, provider responses
  or mutable approvals locally. Errors such as HTTP 409 integrity failures
  remain visible; they are never converted to empty results.
- Reads are limited to admins/superadmins by both UI and ASP.NET policy.
  A normal user sees an access-denied panel and triggers no comparison API.
- Styling follows the shared enterprise workbench and responsive design
  without stretching full-width data tables; the ref table alone scrolls
  horizontally on smaller screens. Italian and English are supported.

## Tests

Playwright contract tests exercise the actual React screen with mocked
authenticated API boundaries:
1. Admin entry, invalid/valid UUID selection, SHA-bound F-031 capture and
   F-032 comparison, all four decision classes, no GitHub write path.
2. Keyset paging and cursor handling.
3. Stale-plan and stale-target proof safety fences, with history review.
4. F-032 integrity conflict reporting.
5. Normal-user RBAC with **zero provider-evidence requests**.

The GitHub Actions workflow continues checking TypeScript/Vite,
Playwright, .NET 10/xUnit, PostgreSQL17 integration and Terraform validation.

## Scope limits

This UI presents saved source and target observations, not live atomic
mirrors or verified Git-object reachability. Its `Current` label means
only that **the backend** considers F-029/F-031/F-032 proofs current.
There is no Git history transfer, no authorization of write permissions,
no push/delete or import to GitHub, no execution queue and no post-write
validation. Source Git ref counts remain limited by F-030/F-032 constraints,
and the backend lists the most recent 20 comparisons for a plan.

Follow-on tasks: an explicit comparison-to-execution gating model,
policy-based independent approvals, read/write capability verification,
a resumable non-destructive Git pack transfer executor with durable
checkpointing, conflict/rollback governance and GitHub post-write checks.
