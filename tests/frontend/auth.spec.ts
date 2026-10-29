import { test, expect } from '@playwright/test';

test('login and forced password change, then dashboard', async ({ page }) => {
  await page.route('**/api/auth/login', async route => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
      accessToken: 'fake-test-token', mustChangePassword: true, username: 'superadmin', role: 'superadmin'
    }) });
  });
  await page.route('**/api/auth/change-password', async route => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
      accessToken: 'new-test-token', mustChangePassword: false, username: 'superadmin', role: 'superadmin'
    }) });
  });
  await page.goto('/');
  await page.getByRole('textbox', { name: /nome utente|username/i }).fill('superadmin');
  await page.getByLabel(/^password$/i).fill('superadmin');
  await page.getByRole('button', { name: /^accedi$|^sign in$/i }).click();
  await expect(page.getByRole('heading', { name: /imposta la nuova password|set your new password/i })).toBeVisible();
  await page.getByLabel(/password attuale|current password/i).fill('superadmin');
  await page.getByLabel(/^(nuova password|new password)$/i).fill('ChangedPassword42!');
  await page.getByRole('button', { name: /salva password|save password/i }).click();
  await expect(page.getByRole('navigation', { name: 'Main navigation' })).toBeVisible();
  await expect(page.getByText(/nessuna scrittura automatica|no automatic writes/i).first()).toBeVisible();
});

test('theme and language can be switched without a server', async ({ page }) => {
  await page.goto('/');
  const initial = await page.locator('html').getAttribute('data-theme');
  await page.getByRole('button', { name: /cambia tema|switch theme/i }).click();
  await expect(page.locator('html')).not.toHaveAttribute('data-theme', initial ?? '');
  await page.getByRole('button', { name: /lingua|language/i }).click();
  await expect(page.getByRole('heading', { name: /welcome back|bentornato/i })).toBeVisible();
});
