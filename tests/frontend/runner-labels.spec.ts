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
  await expect(page.getByText(/etichette dei runner|runner labels/i)).toHaveCount(0);
});

test('an answer from before labels existed has none to show', async ({ page }) => {
  await setup(page, withLabels());
  await open(page);
  await propose(page);
  await expect(page.getByText(/segreti|secrets/i).first()).toBeVisible();
  await expect(page.getByText(/etichette dei runner|runner labels/i)).toHaveCount(0);
});
