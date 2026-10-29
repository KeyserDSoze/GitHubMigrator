export type Session = { accessToken: string; mustChangePassword: boolean; username: string; role: string };
export type Connection = {
  id: string; name: string; providerKey: string; direction: 'source' | 'destination';
  organization: string; createdAtUtc: string; validatedAtUtc: string | null;
  validationCode: string; authenticatedAs: string | null; permissionAssessment: string;
};
export type ConnectionDraft = { name: string; providerKey: string; organization: string; personalAccessToken: string };
export type ValidationResult = {
  isValid: boolean; code: string; authenticatedAs: string | null; permissionAssessment: string
};
export type DiscoverySummary = {
  id: string; connectionId: string; sourceProvider: string; organization: string; collectedAtUtc: string;
  completeness: 'complete' | 'partial'; projectCount: number; repositoryCount: number;
  refCount: number; identityCount: number; issueCount: number;
  groupCount?: number; membershipCount?: number; aclEntryCount?: number;
  securityCoverage?: SecurityCoverage;
  identitiesComplete: boolean; permissionsComplete: boolean;
};
export type DiscoveredRepository = {
  providerId: string; projectId: string; name: string; versionControl: number; sourcePath: string | null
};
export type SecurityCoverage = {
  groupsComplete: boolean; membershipsComplete: boolean; aclsCollected: boolean;
  effectivePermissionsVerified: boolean;
};
export type SecurityGroup = { descriptor: string; displayName: string; legacyDescriptor: string | null };
export type SecurityMembership = { groupDescriptor: string; memberDescriptor: string };
export type AclEvidence = {
  repositoryId: string; securityToken: string; securityDescriptor: string;
  allowMask: number; denyMask: number; inheritPermissions: boolean; scope: 'project' | 'repository';
};
export type DiscoveryDetail = {
  id: string; connectionId: string; organization: string; collectedAtUtc: string;
  snapshot: {
    projects: { providerId: string; name: string; repositories: DiscoveredRepository[] }[];
    identities: { providerId: string; displayName: string; email: string | null; legacyDescriptor?: string | null }[];
  };
  refs: { repositoryId: string; name: string; objectId: string }[];
  issues: { code: string; projectId: string | null; repositoryId: string | null }[];
  groups?: SecurityGroup[]; memberships?: SecurityMembership[];
  aclEvidence?: AclEvidence[]; securityCoverage?: SecurityCoverage;
  identitiesComplete: boolean; permissionsComplete: boolean;
};
export type IdentityMap = { sourceIdentityId: string; gitHubLogin: string };
export type IdentityDraft = {
  snapshotId: string; destinationConnectionId: string; targetOrganization: string;
  revision: number; updatedAtUtc: string; mappings: IdentityMap[];
};
export type IdentityPreviewIssue = { code: string; sourceIdentityId: string | null };
export type IdentityPreview = {
  discoveredIdentities: number; mappedIdentities: number; unmappedIdentities: number;
  unresolvedAclPrincipals: number; sourceIdentitiesComplete: boolean;
  effectivePermissionsVerified: boolean; targetAccountsVerified: boolean;
  readyForGrants: boolean; issues: IdentityPreviewIssue[];
};
export type IdentityReview = { draft: IdentityDraft; preview: IdentityPreview };

const base = import.meta.env.VITE_API_URL ?? '';

async function call<T>(path: string, options: RequestInit): Promise<T> {
  let response: Response;
  try {
    response = await fetch(base + path, { ...options, headers: { 'Content-Type': 'application/json', ...options.headers } });
  } catch {
    throw new Error('Network error - verify that the API is running.');
  }
  if (!response.ok) {
    const problem = await response.json().catch(() => null) as { title?: string; error?: string } | null;
    throw new Error(problem?.error ?? problem?.title ?? `HTTP ${response.status}`);
  }
  if (response.status === 204) return undefined as T;
  return response.json() as Promise<T>;
}

const withToken = (token: string) => ({ Authorization: `Bearer ${token}` });
export const api = {
  login: (username: string, password: string) =>
    call<Session>('/api/auth/login', { method: 'POST', body: JSON.stringify({ username, password }) }),
  changePassword: (token: string, currentPassword: string, newPassword: string) =>
    call<Session>('/api/auth/change-password', {
      method: 'POST', headers: withToken(token),
      body: JSON.stringify({ currentPassword, newPassword })
    }),
  createUser: (token: string, username: string, temporaryPassword: string, role: string) =>
    call<{ id: string }>('/api/users', {
      method: 'POST', headers: withToken(token),
      body: JSON.stringify({ username, temporaryPassword, role })
    }),
  listConnections: (token: string) =>
    call<Connection[]>('/api/connections', { headers: withToken(token) }),
  validateConnection: (token: string, draft: ConnectionDraft) =>
    call<ValidationResult>('/api/connections/validate', {
      method: 'POST', headers: withToken(token), body: JSON.stringify(draft)
    }),
  saveConnection: (token: string, draft: ConnectionDraft) =>
    call<Connection>('/api/connections', {
      method: 'POST', headers: withToken(token), body: JSON.stringify(draft)
    }),
  verifyConnection: (token: string, id: string) =>
    call<Connection>(`/api/connections/${encodeURIComponent(id)}/verify`, {
      method: 'POST', headers: withToken(token)
    }),
  rotateCredential: (token: string, id: string, personalAccessToken: string) =>
    call<Connection>(`/api/connections/${encodeURIComponent(id)}/credential`, {
      method: 'PUT', headers: withToken(token), body: JSON.stringify({ personalAccessToken })
    }),
  listDiscoveries: (token: string) =>
    call<DiscoverySummary[]>('/api/discoveries', { headers: withToken(token) }),
  getDiscovery: (token: string, id: string) =>
    call<DiscoveryDetail>(`/api/discoveries/${encodeURIComponent(id)}`, { headers: withToken(token) }),
  scanDiscovery: (token: string, connectionId: string) =>
    call<DiscoverySummary>('/api/discoveries', {
      method: 'POST', headers: withToken(token), body: JSON.stringify({ connectionId })
    }),
  getIdentityReview: (token: string, snapshotId: string, destinationConnectionId: string) =>
    call<IdentityReview>(`/api/identity-mappings/${encodeURIComponent(snapshotId)}?destinationConnectionId=${encodeURIComponent(destinationConnectionId)}`,
      { headers: withToken(token) }),
  saveIdentityMappings: (token: string, snapshotId: string, destinationConnectionId: string,
      expectedRevision: number, mappings: IdentityMap[]) =>
    call<IdentityReview>(`/api/identity-mappings/${encodeURIComponent(snapshotId)}`,
      { method: 'PUT', headers: withToken(token),
        body: JSON.stringify({ destinationConnectionId, expectedRevision, mappings }) }),
  deleteConnection: (token: string, id: string) =>
    call<void>(`/api/connections/${encodeURIComponent(id)}`, {
      method: 'DELETE', headers: withToken(token)
    })
};
