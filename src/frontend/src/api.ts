export type Session = { accessToken: string; mustChangePassword: boolean; username: string; role: string };
export type Connection = {
  id: string; name: string; providerKey: string; direction: 'source' | 'destination';
  organization: string; createdAtUtc: string; validatedAtUtc: string | null;
  validationCode: string; authenticatedAs: string | null; permissionAssessment: string;,
  listConnections: (token: string) =>
    call<Connection[]>('/api/connections', { headers: { Authorization: `Bearer ${token}` } }),
  validateConnection: (token: string, draft: ConnectionDraft) =>
    call<ValidationResult>('/api/connections/validate', {
      method: 'POST', headers: { Authorization: `Bearer ${token}` }, body: JSON.stringify(draft)
    }),
  saveConnection: (token: string, draft: ConnectionDraft) =>
    call<Connection>('/api/connections', {
      method: 'POST', headers: { Authorization: `Bearer ${token}` }, body: JSON.stringify(draft)
    }),
  verifyConnection: (token: string, id: string) =>
    call<Connection>(`/api/connections/${encodeURIComponent(id)}/verify`, {
      method: 'POST', headers: { Authorization: `Bearer ${token}` }
    }),
  rotateCredential: (token: string, id: string, personalAccessToken: string) =>
    call<Connection>(`/api/connections/${encodeURIComponent(id)}/credential`, {
      method: 'PUT', headers: { Authorization: `Bearer ${token}` }, body: JSON.stringify({ personalAccessToken })
    }),
  deleteConnection: (token: string, id: string) =>
    call<void>(`/api/connections/${encodeURIComponent(id)}`, {
      method: 'DELETE', headers: { Authorization: `Bearer ${token}` }
    })
};
export type ConnectionDraft = { name: string; providerKey: string; organization: string; personalAccessToken: string };
export type ValidationResult = { isValid: boolean; code: string; authenticatedAs: string | null; permissionAssessment: string };
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

export const api = {
  login: (username: string, password: string) =>
    call<Session>('/api/auth/login', { method: 'POST', body: JSON.stringify({ username, password }) }),
  changePassword: (token: string, currentPassword: string, newPassword: string) =>
    call<Session>('/api/auth/change-password', {
      method: 'POST', headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify({ currentPassword, newPassword })
    }),
  createUser: (token: string, username: string, temporaryPassword: string, role: string) =>
    call<{ id: string }>('/api/users', {
      method: 'POST', headers: { Authorization: `Bearer ${token}` },
      body: JSON.stringify({ username, temporaryPassword, role })
    })
};
