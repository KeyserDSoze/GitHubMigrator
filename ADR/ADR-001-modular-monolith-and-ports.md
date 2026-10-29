# ADR-001: Modular monolith and canonical migration model

Status: Accepted (initial foundation)

Context: Sources (Azure DevOps Services/Server/TFS and future providers) and destinations (GitHub Enterprise Cloud/Server and future targets) differ in capabilities and semantics.

Decision: One Migration bounded context, .NET 10 DDD modular monolith. Stable business/domain abstractions for provider discovery, mapping and execution. Source snapshots immutable; user plans mutable with optimistic concurrency; large artifacts in Blob Storage; repositories transported through Git and dedicated workers.

Consequences: Enables new providers without rewriting use cases. Avoid false claim of common TFVC/Git semantics. Workers, queue and feature adapters added incrementally as tested.
