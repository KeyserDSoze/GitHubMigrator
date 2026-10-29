import { expect, test } from '@playwright/test';

test('admin can verify and save Azure DevOps connection without exposing PAT', async ({ page }) => {
  const records: Record<string, unknown>[] = [];
  await page.route('**/api/auth/login', route => route.fulfill({ json: {
    accessToken: 'test-token', mustChangePassword: false, username: 'superadmin', role: 'superadmin'
  } }));
  await page.route('**/api/connections/validate', async route => {
    const draft = route.request().postDataJSON() as { personalAccessToken: string };
    expect(draft.personalAccessToken).toBe('not-a-real-PAT');
    await route.fulfill({ json: { isValid: true, code: 'Verified', permissionAssessment: 'organization_projects_read', authenticatedAs: null } });
  });
  await page.route('**/api/connections', async route => {
    if (route.request().method() === 'GET') {
      await route.fulfill({ json: records }); return;
    }
    const draft = route.request().postDataJSON() as { personalAccessToken: string; name: string; providerKey: string; organization: string };
    expect(draft.personalAccessToken).toBe('not-a-real-PAT');
    const saved = {
      id: 'c3e216d6-8302-470e-a21c-fb9b1b7c27ae', name: draft.name, providerKey: draft.providerKey,
      direction: 'source', organization: draft.organization, createdAtUtc: new Date().toISOString(),
      validatedAtUtc: new Date().toISOString(), validationCode: 'Verified', authenticatedAs: null,
      permissionAssessment: 'organization_projects_read'
    };
    records.push(saved);
    await route.fulfill({ status: 201, json: saved });
  });
  await page.goto('/');
  await page.getByRole('textbox', { name: /nome utente|username/i }).fill('superadmin');
  await page.getByLabel(/^password$/i).fill('password');
  await page.getByRole('button', { name: /^accedi$|^sign in$/i }).click();
  await page.getByRole('button', { name: /connessioni|connections/i }).click();
  await expect(page.getByRole('heading', { name: /nessuna connessione|no connections/i })).toBeVisible();
  await page.getByLabel(/nome connessione|connection name/i).fill('Source A');
  await page.getByLabel(/^organizzazione$|^organization$/i).fill('my-org');
  await page.getByLabel(/personal access token/i).fill('not-a-real-PAT');
  await page.getByRole('button', { name: /testa connessione|test connection/i }).click();
  await expect(page.getByRole('status')).toContainText(/verificat|validat/i);
  await page.getByRole('button', { name: /salva connessione|save connection/i }).click();
  await expect(page.getByRole('heading', { name: 'Source A' })).toBeVisible();
  await expect(page.getByLabel(/personal access token/i)).toHaveValue('');
  await expect(page.getByText('not-a-real-PAT')).toHaveCount(0);
});

test('regular platform user cannot access connection management', async ({ page }) => {
  await page.route('**/api/auth/login', route => route.fulfill({ json: {
    accessToken: 'user-token', mustChangePassword: false, username: 'viewer', role: 'user'
  } }));
  let outbound = 0;
  await page.route('**/api/connections', route => { outbound++; return route.fulfill({ status: 403 }); });
  await page.goto('/');
  await page.getByRole('textbox', { name: /nome utente|username/i }).fill('viewer');
  await page.getByLabel(/^password$/i).fill('password');
  await page.getByRole('button', { name: /^accedi$|^sign in$/i }).click();
  await page.getByRole('button', { name: /connessioni|connections/i }).click();
  await expect(page.getByText(/solo gli amministratori|only administrators/i)).toBeVisible();
  expect(outbound).toBe(0);
});
