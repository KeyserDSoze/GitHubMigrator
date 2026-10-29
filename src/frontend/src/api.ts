export type Session = { accessToken: string; mustChangePassword: boolean; username: string; role: string };
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
