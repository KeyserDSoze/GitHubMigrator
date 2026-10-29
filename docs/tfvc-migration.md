# TFVC → Git history fidelity

Git and TFVC have fundamentally different semantics. The native Azure DevOps TFVC import is restricted to a root or a branch, at most 180 days of history and a 1 GB import. Advanced migrations can evaluate the community `git-tfs` tool to recover changesets and multiple branches/merges (subject to limitations). Read:
- https://learn.microsoft.com/en-us/azure/devops/repos/git/import-from-tfvc
- https://github.com/git-tfs/git-tfs/blob/master/doc/usecases/migrate_tfs_to_git.md

## Proposed strategies (future implementation)

1. `TipOnly`: latest source snapshot as a single Git commit, fast and explicitly lossy.
2. `FullHistoryBestEffort`: isolated compatible git-tfs worker and per-changeset commit generation where feasible; preserve author, date, message and original changeset ID in trailers. Multiple branches are best effort; not every TFVC merge becomes a faithful Git merge.
3. `ArchiveAndTip`: complete auditable TFVC export/manifest (artifact storage) plus Git tip, when fidelity cannot be proven.

Assess branch and changeset count, topology, deleted items, renames, labels, baseless merges, cloaks, binary/LFS files and author mappings *before* choosing a strategy. Persist a mapping `TFVC changeset → Git commit SHA` and exceptions, plus compare file trees, refs and import statistics. Never claim one-to-one fidelity for merges, renames, path permissions or labels. Ensure the source remains readable/archived where possible.

A git-tfs runner has OS/runtime and compatibility constraints: it must be validated against each TFS/Azure DevOps Server version; a Linux container cannot automatically be assumed sufficient. A Windows worker could be required. This is a design investigation, not a built conversion feature.
