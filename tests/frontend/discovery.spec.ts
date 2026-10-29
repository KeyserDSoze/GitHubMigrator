import { expect, test } from '@playwright/test';

const id = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const connectionId = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
const summary = {
  id, connectionId, sourceProvider: 'azure-devops-services', organization: 'demo',
  collectedAtUtc: '2026-10-29T10:00:00Z', completeness: 'partial',
  projectCount: 1, repositoryCount: 2, refCount: 2, identityCount: 1,
  issueCount: 2, identitiesComplete: true, permissionsComplete: false
};
const detail = {
  id, connectionId, sourceProvider: 'azure-devops-services', organization: 'demo',
  collectedAtUtc: summary.collectedAtUtc,
  snapshot: {
    projects: [{ providerId: 'proj-1', name: 'Payments', repositories: [
      { providerId: 'repo-1', projectId: 'proj-1', name: 'Api', versionControl: 0, sourcePath: null },
      { providerId: 'tfvc:proj-1:$/Payments', projectId: 'proj-1', name: 'Payments', versionControl: 1, sourcePath: '$/Payments' }
    ] }],
    identities: [{ providerId: 'aad.example', displayName: 'Alice', email: 'alice@example.invalid' }]
  },
  refs: [
    { repositoryId: 'repo-1', name: 'refs/heads/main', objectId: 'sha1' },
    { repositoryId: 'repo-1', name: 'refs/heads/dev', objectId: 'sha2' }
  ],
  issues: [{ code: 'PermissionsNotCollected', projectId: null, repositoryId: null },
    { code: 'TfvcPathsRequireFullInventory', projectId: 'proj-1', repositoryId: null }],
  identitiesComplete: true, permissionsComplete: false
};

async function mockLogin(page: import('@playwright/test').Page, role: string) {
  await page.route('**/api/auth/login', route => route.fulfill({ json: {
    accessToken: 'mock-access-token', mustChangePassword: false,
    username: role === 'admin' ? 'admin' : 'viewer', role
  } }));
  await page.goto('/');
  await page.getByRole('textbox', { name: /nome utente|username/i }).fill(role);
  await page.getByLabel(/^password$/i).fill('some-test-password');
  await page.getByRole('button', { name: /^accedi$|^sign in$/i }).click();
}

test('admin discovers Git/TFVC, saves snapshot and reviews partial permissions', async ({ page }) => {
  let scanRequests = 0;
  await page.route('**/api/connections', route => route.fulfill({ json: [{
    id: connectionId, name: 'Demo DevOps', providerKey: 'azure-devops-services',
    direction: 'source', organization: 'demo',
    validationCode: 'Verified'
  }] }));
  await page.route('**/api/discoveries', route => {
    if (route.request().method() === 'POST') {
      expect(route.request().postDataJSON()).toEqual({ connectionId });
      scanRequests++;
      return route.fulfill({ status: 201, json: summary });
    }
    return route.fulfill({ json: [] });
  });
  await page.route(`**/api/discoveries/${id}`, route => route.fulfill({ json: detail }));
  await mockLogin(page, 'admin');
  await page.getByRole('button', { name: /^discovery$/i }).click();
  await expect(page.getByText(/nessun inventario salvato|no saved inventories/i).first()).toBeVisible();
  await page.getByRole('button', { name: /avvia discovery|run discovery/i }).click();
  await expect(page.getByRole('heading', { name: 'Payments' })).toBeVisible();
  await expect(page.getByText('Api', { exact: true })).toBeVisible();
  await expect(page.getByText('TFVC', { exact: true })).toBeVisible();
  await expect(page.getByText(/permessi effettivi|effective repository permissions/i)).toBeVisible();
  await page.getByRole('button', { name: /^identità$|^identities$/i }).click();
  await expect(page.getByText('Alice', { exact: true }).first()).toBeVisible();
  await page.getByRole('button', { name: /^(avvisi di copertura|coverage warnings)$/i }).click();
  await expect(page.getByText(/PermissionsNotCollected/i)).toBeVisible();
  expect(scanRequests).toBe(1);
});

test('regular platform user cannot discover or read snapshots', async ({ page }) => {
  let requests = 0;
  await page.route('**/api/discoveries**', route => { requests++; return route.fulfill({ status: 403 }); });
  await page.route('**/api/connections', route => { requests++; return route.fulfill({ status: 403 }); });
  await mockLogin(page, 'user');
  await page.getByRole('button', { name: /^discovery$/i }).click();
  await expect(page.getByText(/solo gli amministratori|only platform administrators/i)).toBeVisible();
  expect(requests).toBe(0);
});
