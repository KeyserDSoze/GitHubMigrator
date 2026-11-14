import { expect, test } from '@playwright/test';
import { deriveButton, detail, open, propose, setup, sha } from './proposal-fixtures';

const provenance = (extra: Record<string, unknown> = {}) => ({
  aiConnectionId: 'a', provider: 'OpenAICompatible', host: 'llm.corp.example', model: 'gpt-x', reportedModel: null, promptSha256: 'ab'.repeat(32), planSha256: 'cd'.repeat(32),
  policyVersion: '2026-11-1', actionCatalogVerifiedOn: '2026-11-13', redactions: [], totalPromptTokens: 10, totalCompletionTokens: 5, attempts: [{ number: 1, outcome: 'GatesPassed' }],
  generatedAtUtc: '2026-11-13T12:00:00Z', ...extra
});

const classicProvenance = () => provenance({
  sourceOrigin: 'classic-definition',
  sourceNotes: ['Continuous integration trigger, not carried over: branches [+refs/heads/master]', 'Schedule, not carried over: Mon,Tue,Wed,Thu,Fri at 02:00 (UTC)', '1 task(s) could not be named, shown by id: 99999999-0000-0000-0000-000000000001.']
});

const approved = (extra: Record<string, unknown> = {}) => detail({ state: 'Approved', approvedSha256: sha, canApprove: false, findings: [], ...extra });

test('a conversion made from a classic definition says so and lists what the rendering could not carry over', async ({ page }) => {
  await setup(page, detail({ provenance: classicProvenance() }));
  await open(page);
  await propose(page);

  await expect(page.getByText(/definizione di build classica|classic \(designer\) build definition, rendered as text/i)).toBeVisible();
  await expect(page.getByText(/non c.era un file yaml|there was no yaml file/i)).toBeVisible();
  await expect(page.getByText('Continuous integration trigger, not carried over: branches [+refs/heads/master]')).toBeVisible();
  await expect(page.getByText('Schedule, not carried over: Mon,Tue,Wed,Thu,Fri at 02:00 (UTC)')).toBeVisible();
  await expect(page.getByText(/could not be named, shown by id: 99999999/)).toBeVisible();
});

test('a conversion from a YAML pipeline, or from before classic ones could be converted, shows no source line', async ({ page }) => {
  await setup(page, detail({ provenance: provenance() }));
  await open(page);
  await propose(page);
  await expect(page.getByText(/model|modello/i).first()).toBeVisible(); // the provenance is there
  await expect(page.getByText(/definizione di build classica|classic \(designer\) build definition/i)).toHaveCount(0);
  await expect(page.locator('.conversion-source-notes')).toHaveCount(0);
});

test('the triggers of a classic conversion are never offered for derivation, and the screen says why', async ({ page }) => {
  const store = await setup(page, approved({ provenance: classicProvenance() }));
  await open(page);
  await propose(page);

  const section = page.getByLabel(/^trigger azure$|^azure triggers$/i);
  await expect(section).toBeVisible();
  await expect(section.getByText(/non vengono derivati|are not derived/i)).toBeVisible();
  await expect(section.getByText(/workflow_dispatch/)).toBeVisible();
  await expect(deriveButton(page)).toHaveCount(0);
  expect(store.derives).toEqual([]);
});

test('an approved conversion that is not from a classic definition still offers its triggers', async ({ page }) => {
  await setup(page, approved({ provenance: provenance() }));
  await open(page);
  await propose(page);
  await expect(deriveButton(page)).toBeVisible();
  await expect(page.getByText(/non vengono derivati|are not derived/i)).toHaveCount(0);
});
