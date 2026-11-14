import { expect, test } from '@playwright/test';
import { detail, open, propose, setup } from './proposal-fixtures';

const provenance = (extra: Record<string, unknown> = {}) => ({
  aiConnectionId: 'a', provider: 'OpenAICompatible', host: 'llm.corp.example', model: 'gpt-x', reportedModel: null, promptSha256: 'ab'.repeat(32), planSha256: 'cd'.repeat(32),
  policyVersion: '2026-11-1', actionCatalogVerifiedOn: '2026-11-13', redactions: [], totalPromptTokens: 10, totalCompletionTokens: 5, attempts: [{ number: 1, outcome: 'GatesPassed' }],
  generatedAtUtc: '2026-11-13T12:00:00Z', ...extra
});

const includeTemplates = (page: import('@playwright/test').Page) => page.getByRole('checkbox', { name: /leggi anche i template|also read the templates/i });

test('templates are not read unless a person asks, and asking is part of the request', async ({ page }) => {
  const store = await setup(page, detail({}));
  await open(page);
  await expect(includeTemplates(page)).not.toBeChecked();
  await expect(page.getByText(/senza questa opzione al modello si dice che non può vederli|without this the model is told it cannot see them/i)).toBeVisible();
  await propose(page);
  expect(store.converts[0]).not.toHaveProperty('includeTemplates');

  await includeTemplates(page).check();
  await propose(page);
  expect(store.converts[1].includeTemplates).toBe(true);
});

test('what was sent of the templates and what was not, with the reason, is listed with the proposal', async ({ page }) => {
  await setup(page, detail({ provenance: provenance({
    templates: [
      { repository: 'self', path: '/pipelines/templates/dotnet-quality.yml', ref: null, sha256: 'ab'.repeat(32), bytes: 1234 },
      { repository: 'Orion Platform/orion-templates', path: '/steps/security-scan.yml', ref: 'refs/heads/main', sha256: 'cd'.repeat(32), bytes: 800 }
    ],
    unresolvedTemplates: [
      { reference: 'jobs/build.yml@shared', reason: 'NotReadable' }, { reference: '${{ variables.x }}.yml', reason: 'ExpressionPath' }, { reference: 'weird.yml@x', reason: 'SomethingNew' }
    ]
  }) }));
  await open(page);
  await propose(page);

  const sent = page.getByText(/template inviati|templates sent/i);
  await expect(sent).toBeVisible();
  await expect(page.getByText(/self:\/pipelines\/templates\/dotnet-quality\.yml \(1234 B\)/)).toBeVisible();
  await expect(page.getByText(/Orion Platform\/orion-templates:\/steps\/security-scan\.yml @ refs\/heads\/main \(800 B\)/)).toBeVisible();
  const notSent = page.getByText(/template non inviati|templates not sent/i);
  await expect(notSent).toBeVisible();
  await expect(page.getByText(/jobs\/build\.yml@shared — (non si è potuto leggere|could not be read)/)).toBeVisible();
  await expect(page.getByText(/il percorso è calcolato|its path is computed/i)).toBeVisible();
  await expect(page.getByText(/weird\.yml@x — SomethingNew/)).toBeVisible(); // a reason this screen does not know is shown as it is, not hidden
});

test('a proposal without templates, or from before they existed, lists none', async ({ page }) => {
  await setup(page, detail({ provenance: provenance() }));
  await open(page);
  await propose(page);
  await expect(page.getByText(/modello|model/i).first()).toBeVisible(); // the provenance is shown
  await expect(page.getByText(/template inviati|templates sent/i)).toHaveCount(0);
  await expect(page.getByText(/template non inviati|templates not sent/i)).toHaveCount(0);
});
