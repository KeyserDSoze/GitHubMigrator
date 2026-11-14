import { expect, test } from '@playwright/test';
import { detail, open, propose, setup } from './proposal-fixtures';

const withLabels = (labels?: string[]) => detail({
  plan: { secrets: [{ name: 'DEPLOY_PASSWORD', origin: 'pipeline variable' }], variables: [], connections: [], notes: [], ...(labels ? { runnerLabels: labels } : {}) }
});

test('the runner labels a self-hosted pool demands are shown with the plan, and what they do and do not mean', async ({ page }) => {
  await setup(page, withLabels(['docker', 'npm']));
  await open(page);
  await propose(page);

  const line = page.getByText(/etichette dei runner|runner labels/i).first();
  await expect(line).toBeVisible();
  await expect(page.getByText('docker, npm')).toBeVisible();
  await expect(page.getByText(/il destinatario deve avere runner con queste etichette|destination needs runners that have them/i)).toBeVisible();
});

test('a plan without labels shows no runner labels line, and neither does an older answer that has no such field', async ({ page }) => {
  await setup(page, withLabels([]));
  await open(page);
  await propose(page);
  await expect(page.getByText(/segreti|secrets/i).first()).toBeVisible(); // the plan itself is shown
  await expect(page.locator('strong', { hasText: /^(etichette dei runner|runner labels):$/i })).toHaveCount(0); // the plan's own line (the extra-labels field has a label of its own)
});

test('an answer from before labels existed has none to show', async ({ page }) => {
  await setup(page, withLabels());
  await open(page);
  await propose(page);
  await expect(page.getByText(/segreti|secrets/i).first()).toBeVisible();
  await expect(page.locator('strong', { hasText: /^(etichette dei runner|runner labels):$/i })).toHaveCount(0); // the plan's own line (the extra-labels field has a label of its own)
});

const extraLabels = (page: import('@playwright/test').Page) => page.getByRole('textbox', { name: /etichette extra dei runner|extra runner labels/i });

test('labels given for one conversion are sent as a list and the extra labels of the plan are shown apart from the demanded ones', async ({ page }) => {
  const store = await setup(page, detail({
    plan: { secrets: [], variables: [], connections: [], notes: [], runnerLabels: ['docker'], extraRunnerLabels: ['orion-build', 'gpu'] }
  }));
  await open(page);
  await extraLabels(page).fill('gpu, orion-build;  linux-x64');
  await propose(page);

  expect(store.converts).toHaveLength(1);
  expect(store.converts[0].runnerLabels).toEqual(['gpu', 'orion-build', 'linux-x64']);
  await expect(page.getByText('orion-build, gpu')).toBeVisible();
  await expect(page.getByText(/non richieste dalla pipeline|not demanded by the pipeline/i)).toBeVisible();
  await expect(page.locator('p', { has: page.locator('strong', { hasText: /^(etichette dei runner|runner labels):$/i }) })).toContainText('docker'); // what the pool demanded is still shown, apart
});

test('without labels the request carries none and nothing extra is shown', async ({ page }) => {
  const store = await setup(page, detail({ plan: { secrets: [], variables: [], connections: [], notes: [] } }));
  await open(page);
  await propose(page);
  expect(store.converts[0]).not.toHaveProperty('runnerLabels');
  await expect(page.getByText(/etichette extra dei runner|extra runner labels/i)).toHaveCount(1); // only the field's own label
  await expect(page.getByText(/non richieste dalla pipeline|not demanded by the pipeline/i)).toHaveCount(0);
});

test('a label that is not a plain name is explained, not reported as a failed request', async ({ page }) => {
  await setup(page, detail({}), { convertRefuse: { status: 400, body: { error: 'InvalidRunnerLabel' } } });
  await open(page);
  await extraLabels(page).fill('Agent.OS -equals Linux');
  await propose(page);
  await expect(page.getByRole('alert').filter({ hasText: /nome semplice|plain name/i })).toBeVisible();
  await expect(page.getByText(/non è stato letto né inviato nulla|nothing was read or sent/i)).toBeVisible();
  await expect(page.getByText(/la richiesta di conversione non è riuscita|the conversion request failed/i)).toHaveCount(0);
});
