import { expect, test } from '@playwright/test';
import { destinationId, detail, open, propose, publicationPlan, setup, sha } from './proposal-fixtures';

const approved = () => detail({
  state: 'Approved', approvedSha256: sha, canApprove: false, findings: [],
  events: [
    { id: 1, type: 'Created', actor: 'author', occurredAtUtc: '2026-11-13T12:00:00Z', workflowSha256: sha, note: null, reportSha256: null, policyVersion: null, acknowledgedWarnings: [] },
    { id: 2, type: 'Approved', actor: 'second-person', occurredAtUtc: '2026-11-13T12:30:00Z', workflowSha256: sha, note: null, reportSha256: 'c'.repeat(64), policyVersion: '2026-11-1', acknowledgedWarnings: [] }
  ]
});

const panel = (page: import('@playwright/test').Page) => page.getByLabel(/prepara la pull request|prepare the pull request/i);
const repositoryField = (page: import('@playwright/test').Page) => panel(page).getByRole('textbox', { name: /repository di destinazione|destination repository/i });
const baseField = (page: import('@playwright/test').Page) => panel(page).getByRole('textbox', { name: /branch di base|base branch/i });
const plan = (page: import('@playwright/test').Page) => panel(page).getByRole('button', { name: /pianifica la pull request|plan the pull request/i });

test('an approved proposal can be planned against the destination, and the screen says nothing is written', async ({ page, context }) => {
  await context.grantPermissions(['clipboard-read', 'clipboard-write']);
  const store = await setup(page, approved());
  await open(page);
  await propose(page);

  await expect(panel(page)).toBeVisible();
  await expect(plan(page)).toBeDisabled(); // no repository yet
  await repositoryField(page).fill('orion-api');
  await expect(plan(page)).toBeEnabled();
  await plan(page).click();

  await expect(page.getByText(/nulla d.intralcio|nothing stands in the way/i)).toBeVisible();
  await expect(page.getByText(/nessuna scrittura è stata fatta|no write was made/i)).toBeVisible();
  await expect(panel(page).getByText('migrate/azdo-123-abababab').first()).toBeVisible();
  await expect(panel(page).getByText('.github/workflows/azdo-123-orion-api.yml').first()).toBeVisible();
  await expect(panel(page).getByText('d'.repeat(64))).toBeVisible();
  await expect(panel(page).getByText(/Add GitHub Actions workflow for Azure pipeline #123/)).toBeVisible();
  await expect(panel(page).getByText(/gh pr create --base 'main'/)).toBeVisible();
  // the request names the connection and the repository, and no base branch unless one was typed
  expect(store.plans).toEqual([{ destinationConnectionId: destinationId, repository: 'orion-api', baseBranch: null }]);

  // what a person needs is one click from the clipboard
  await panel(page).getByRole('button', { name: /copia|copy/i }).nth(1).click();
  expect(await page.evaluate(() => navigator.clipboard.readText())).toContain('## Azure Pipelines → GitHub Actions (proposal)');

  const download = page.waitForEvent('download');
  await panel(page).getByRole('button', { name: /scarica|download/i }).click();
  const file = await download;
  expect(file.suggestedFilename()).toBe('azdo-123-orion-api.yml');
});

test('a typed base branch is sent, and what stands in the way is listed and never presented as ready', async ({ page }) => {
  const store = await setup(page, approved(), { plan: publicationPlan('orion-api', {
    ready: false,
    findings: [
      { code: 'WorkflowPathOccupied', severity: 'Blocker', message: 'A different file already exists.' },
      { code: 'OpenPullRequestExists', severity: 'Blocker', message: 'An open pull request already uses this branch (#7).' }
    ]
  }) });
  await open(page);
  await propose(page);
  await repositoryField(page).fill('orion-api');
  await baseField(page).fill('release/2026');
  await plan(page).click();

  await expect(page.getByText(/qualcosa è d.intralcio|something stands in the way/i)).toBeVisible();
  await expect(page.getByText('WorkflowPathOccupied')).toBeVisible();
  await expect(page.getByText('OpenPullRequestExists')).toBeVisible();
  await expect(page.getByText(/nulla d.intralcio|nothing stands in the way/i)).toHaveCount(0);
  expect(store.plans).toEqual([{ destinationConnectionId: destinationId, repository: 'orion-api', baseBranch: 'release/2026' }]);
});

test('a workflow that is already on the base branch is "nothing to publish", not "ready"', async ({ page }) => {
  await setup(page, approved(), { plan: publicationPlan('orion-api', { ready: false, findings: [{ code: 'AlreadyPublished', severity: 'Info', message: 'already there' }] }) });
  await open(page);
  await propose(page);
  await repositoryField(page).fill('orion-api');
  await plan(page).click();
  await expect(page.getByText(/niente da pubblicare|nothing to publish/i).first()).toBeVisible();
  await expect(page.getByText(/nulla d.intralcio|nothing stands in the way/i)).toHaveCount(0);
});

test('a refusal is explained, leaves no stale plan, and a proposal that is not approved has no such panel', async ({ page }) => {
  await setup(page, approved(), { planRefuse: { status: 409, body: { error: 'ProposalNotApproved' } } });
  await open(page);
  await propose(page);
  await repositoryField(page).fill('orion-api');
  await plan(page).click();
  await expect(page.getByRole('alert')).toContainText(/non è \(più\) approvata|not approved \(any more\)/i);
  await expect(panel(page).getByText(/migrate\/azdo/)).toHaveCount(0);
});

test('only an approved proposal offers a plan', async ({ page }) => {
  await setup(page, detail());
  await open(page);
  await propose(page);
  await expect(page.getByLabel(/revisione umana|human review/i)).toBeVisible();
  await expect(panel(page)).toHaveCount(0);
});
