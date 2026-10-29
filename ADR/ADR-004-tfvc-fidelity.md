# ADR-004: TFVC history is a best-effort translation

Status: Proposed

Decision: Model conversion strategies and report fidelity; investigate git-tfs on supported workers for multi-branch changesets and attempt merge history, but never promise exact conversion. Preserve original changeset IDs and mapping evidence. Offer archived-source fallback.

Consequences: Windows worker may be necessary for compatibility; higher migration cost. Do not implement a naive per-file translation and misrepresent it as faithful history.
