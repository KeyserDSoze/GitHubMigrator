import { expect, test, type Page } from '@playwright/test';

const modelId = '5d3b7b4e-3c6a-4d0e-9f53-0b1a8f6d9a11';

function saved(overrides: Record<string, unknown> = {}) {
  return {
    id: modelId, name: 'Corporate model', provider: 'OpenAICompatible', endpoint: 'https://api.openai.com/v1', model: 'gpt-4.1',
    auth: 'ApiKey', apiVersion: null, tokenParameter: 'MaxTokens', maxOutputTokens: 4096, temperature: 0, timeoutSeconds: 60,
    maxInputBytes: 200000, createdAtUtc: '2026-11-13T09:30:00Z', verifiedAtUtc: '2026-11-13T09:30:00Z', verificationCode: 'Verified',
    verifiedModel: 'gpt-4.1-2025', sendSourceContent: false, consentAtUtc: null, consentBy: null, ...overrides
  };
}

async function signIn(page: Page, role: 'superadmin' | 'admin') {
  await page.route('**/api/auth/login', route => route.fulfill({ json: {
    accessToken: 'test-token', mustChangePassword: false, username: role, role
  } }));
  await page.route('**/api/connections', route => route.fulfill({ json: [] }));
  await page.route('**/api/ai-connections/options', route => route.fulfill({ json: {
    providers: ['OpenAICompatible', 'AzureOpenAI'], allowedHosts: ['api.openai.com', '*.openai.azure.com'], allowHttp: false, managedIdentityAvailable: false
  } }));
  await page.goto('/');
  await page.getByRole('textbox', { name: /nome utente|username/i }).fill(role);
  await page.getByLabel(/^password$/i).fill('password');
  await page.getByRole('button', { name: /^accedi$|^sign in$/i }).click();
  await page.getByRole('button', { name: /connessioni|connections/i }).click();
}

test('admin can test and save an AI model without the key ever coming back', async ({ page }) => {
  const records: Record<string, unknown>[] = [];
  const sent: Record<string, unknown>[] = [];
  await page.route('**/api/ai-connections/validate', async route => {
    sent.push(route.request().postDataJSON() as Record<string, unknown>);
    await route.fulfill({ json: { isValid: true, code: 'Verified', model: 'gpt-4.1-2025', detail: null } });
  });
  await page.route('**/api/ai-connections', async route => {
    if (route.request().method() === 'GET') { await route.fulfill({ json: records }); return; }
    sent.push(route.request().postDataJSON() as Record<string, unknown>);
    const item = saved();
    records.push(item);
    await route.fulfill({ status: 201, json: item });
  });
  await signIn(page, 'superadmin');

  await expect(page.getByRole('heading', { name: /nessun profilo ai|no ai profiles/i })).toBeVisible();
  await expect(page.getByText(/api\.openai\.com, \*\.openai\.azure\.com/)).toBeVisible();
  await page.getByLabel(/nome del profilo ai|ai profile name/i).fill('Corporate model');
  await page.getByLabel(/url dell’endpoint|endpoint url/i).fill('https://api.openai.com/v1');
  await page.getByLabel(/nome del modello o deployment|model or deployment name/i).fill('gpt-4.1');
  await page.getByLabel(/^chiave api$|^api key$/i).fill('sk-not-a-real-key-0123');
  await page.getByRole('button', { name: /prova il modello|test model/i }).click();
  await expect(page.getByRole('status')).toContainText(/gpt-4\.1-2025/);
  await page.getByRole('button', { name: /salva profilo ai|save ai profile/i }).click();

  await expect(page.getByRole('heading', { name: 'Corporate model' })).toBeVisible();
  await expect(page.getByLabel(/^chiave api$|^api key$/i)).toHaveValue('');
  await expect(page.getByText('sk-not-a-real-key-0123')).toHaveCount(0);
  expect(sent).toHaveLength(2);
  for (const body of sent) {
    expect(body.apiKey).toBe('sk-not-a-real-key-0123');
    expect(body.provider).toBe('OpenAICompatible');
    expect(body.auth).toBe('ApiKey');
  }
  await expect(page.getByText(/non viene inviato a questo modello|not sent to this model/i)).toBeVisible();
});

test('a rejected key is explained instead of a bare error code', async ({ page }) => {
  await page.route('**/api/ai-connections/validate', route => route.fulfill({ json: { isValid: false, code: 'Unauthorized', model: null, detail: 'invalid_api_key' } }));
  await page.route('**/api/ai-connections', route => route.fulfill({ json: [] }));
  await signIn(page, 'admin');
  await page.getByLabel(/nome del profilo ai|ai profile name/i).fill('X');
  await page.getByLabel(/url dell’endpoint|endpoint url/i).fill('https://api.openai.com/v1');
  await page.getByLabel(/nome del modello o deployment|model or deployment name/i).fill('gpt-4.1');
  await page.getByLabel(/^chiave api$|^api key$/i).fill('sk-wrong-key-0123456');
  await page.getByRole('button', { name: /prova il modello|test model/i }).click();
  await expect(page.getByRole('alert')).toContainText(/rifiutato la chiave|rejected the api key/i);
});

test('only a superadmin can allow sending pipeline source, after an explicit confirmation', async ({ page }) => {
  let current = saved();
  const calls: boolean[] = [];
  await page.route('**/api/ai-connections', route => route.fulfill({ json: [current] }));
  await page.route(`**/api/ai-connections/${modelId}/source-consent`, async route => {
    const body = route.request().postDataJSON() as { sendSourceContent: boolean };
    calls.push(body.sendSourceContent);
    current = saved({ sendSourceContent: body.sendSourceContent, consentBy: 'superadmin', consentAtUtc: '2026-11-13T10:00:00Z' });
    await route.fulfill({ json: current });
  });
  await signIn(page, 'superadmin');
  const dialogs: string[] = [];
  page.on('dialog', dialog => { dialogs.push(dialog.message()); void dialog.accept(); });

  await page.getByRole('button', { name: /consenti l’invio|allow sending/i }).click();
  await expect(page.getByText(/può essere inviato a questo modello|may be sent to this model/i)).toBeVisible();
  await expect(page.getByText(/consentito da superadmin|allowed by superadmin/i)).toBeVisible();
  expect(calls).toEqual([true]);
  expect(dialogs.join(' ')).toMatch(/segreti oscurati|secrets redacted/i);

  await page.getByRole('button', { name: /interrompi l’invio|stop sending/i }).click();
  await expect(page.getByText(/non viene inviato a questo modello|not sent to this model/i)).toBeVisible();
  expect(calls).toEqual([true, false]);
});

test('a plain admin sees the consent as a superadmin decision and has no way to change it', async ({ page }) => {
  await page.route('**/api/ai-connections', route => route.fulfill({ json: [saved()] }));
  await signIn(page, 'admin');
  await expect(page.getByText(/solo un superadmin|only a superadmin/i)).toBeVisible();
  await expect(page.getByRole('button', { name: /consenti l’invio|allow sending/i })).toHaveCount(0);
});

test('a connection that stopped answering cannot be given consent', async ({ page }) => {
  await page.route('**/api/ai-connections', route => route.fulfill({ json: [saved({ verificationCode: 'Unauthorized', verifiedModel: null })] }));
  await signIn(page, 'superadmin');
  await expect(page.getByRole('button', { name: /consenti l’invio|allow sending/i })).toBeDisabled();
});
