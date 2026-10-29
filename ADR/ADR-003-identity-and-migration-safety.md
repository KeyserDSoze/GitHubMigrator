# ADR-003: Platform identities are not migrated identities

Status: Accepted (initial foundation)

Decision: Separate platform accounts (superadmin, admin, user; local PostgreSQL) from external identities and their GitHub mappings. Seed initial superadmin with forced password change. Permit literal superadmin/superadmin only in Development/test; reject in non-Development. No implicit permission grants for unmapped users. Explicit preview and approval prior to write.

Consequences: Platform RBAC need not mirror Azure DevOps or GitHub RBAC. Scope of roles and guest identity mapping requires future ADRs. Bootstrap secrets and JWT signing material must be managed through Key Vault in Azure.
