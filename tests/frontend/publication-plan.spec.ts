import { expect, test } from '@playwright/test';
import { destinationId, detail, open, propose, publicationPlan, publicationRecord, setup, sha } from './proposal-fixtures';

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

const enabled = { enabled: true, allowed: ['target-org/orion-api'] };
const publishButton = (page: import('@playwright/test').Page) => panel(page).getByRole('button', { name: /pubblica la pull request|publish the pull request/i });
const confirmBox = (page: import('@playwright/test').Page) => panel(page).getByRole('checkbox', { name: /ho letto questo piano|i read this plan/i });

test('the product publishes only what the person confirmed by the hash of the plan, and says that nothing was merged', async ({ page }) => {
  const store = await setup(page, approved(), { publication: enabled });
  await open(page);
  await propose(page);
  await repositoryField(page).fill('orion-api');
  await plan(page).click();

  await expect(panel(page).getByText(/nulla d.intralcio|nothing stands in the way/i)).toBeVisible();
  await expect(publishButton(page)).toBeDisabled(); // not confirmed yet
  await expect(panel(page).getByText(new RegExp('d{12}…'))).toBeVisible(); // the confirmation names the plan by its hash
  await confirmBox(page).check();
  await expect(publishButton(page)).toBeEnabled();
  await publishButton(page).click();

  await expect(panel(page).getByText(/pull request #7 aperta|pull request #7 opened/i)).toBeVisible();
  await expect(panel(page).getByText(/nulla è stato unito|nothing was merged/i)).toBeVisible();
  const link = panel(page).getByRole('link', { name: /aprila su github|open it on github/i });
  await expect(link).toHaveAttribute('href', 'https://github.com/target-org/orion-api/pull/7');
  await expect(link).toHaveAttribute('rel', /noopener/);
  // the request names the plan the person saw: never a plan the server made up in the meantime
  expect(store.publishes).toEqual([{ destinationConnectionId: destinationId, repository: 'orion-api', baseBranch: null, planSha256: 'd'.repeat(64) }]);
  // the attempt is listed, and the button is gone: a plan is not published twice from the screen
  await expect(panel(page).getByText(/tentativi di pubblicazione|publication attempts/i)).toBeVisible();
  await expect(publishButton(page)).toHaveCount(0);
});

test('publishing is offered only when it is enabled, the repository is allowed, nothing is in the way and the user is a superadmin', async ({ page }) => {
  // not enabled on this deployment
  await setup(page, approved());
  await open(page);
  await propose(page);
  await repositoryField(page).fill('orion-api');
  await plan(page).click();
  await expect(panel(page).getByText(/non è abilitata su questa installazione|is not enabled on this deployment/i)).toBeVisible();
  await expect(publishButton(page)).toHaveCount(0);
});

test('a repository that is not on the list, a plan with blockers and a non-superadmin are each told why', async ({ page }) => {
  await setup(page, approved(), { publication: { enabled: true, allowed: ['target-org/somewhere-else'] } });
  await open(page);
  await propose(page);
  await repositoryField(page).fill('orion-api');
  await plan(page).click();
  await expect(panel(page).getByText(/non è nell.elenco dei repository|not on the list of repositories/i)).toBeVisible();
  await expect(publishButton(page)).toHaveCount(0);
});

test('a plan with something in the way cannot be published', async ({ page }) => {
  await setup(page, approved(), { publication: enabled, plan: publicationPlan('orion-api', { ready: false, findings: [{ code: 'HeadBranchExists', severity: 'Blocker', message: 'taken' }] }) });
  await open(page);
  await propose(page);
  await repositoryField(page).fill('orion-api');
  await plan(page).click();
  await expect(panel(page).getByText(/non si può pubblicare finché|nothing can be published until/i)).toBeVisible();
  await expect(publishButton(page)).toHaveCount(0);
});

test('an administrator who is not a superadmin sees the plan but is told who can publish', async ({ page }) => {
  await setup(page, approved(), { publication: enabled, role: 'admin' });
  await open(page);
  await propose(page);
  await repositoryField(page).fill('orion-api');
  await plan(page).click();
  await expect(panel(page).getByText(/solo un superadmin può pubblicare|only a superadmin can publish/i)).toBeVisible();
  await expect(publishButton(page)).toHaveCount(0);
});

test('when the destination changed after the plan, the confirmation is void and the plan has to be made again', async ({ page }) => {
  const store = await setup(page, approved(), { publication: enabled, publishRefuse: { status: 409, body: { error: 'PublicationPlanChanged', details: [] } } });
  await open(page);
  await propose(page);
  await repositoryField(page).fill('orion-api');
  await plan(page).click();
  await confirmBox(page).check();
  await publishButton(page).click();
  await expect(page.getByRole('alert')).toContainText(/destinazione è cambiata|destination changed/i);
  await expect(panel(page).getByText('migrate/azdo-123-abababab')).toHaveCount(0); // the stale plan is gone
  await expect(plan(page)).toBeVisible();
  expect(store.publishes).toHaveLength(1);
});

test('a write that failed halfway says that something may exist and that the product will not retry', async ({ page }) => {
  const failed = publicationRecord({ state: 'Failed', failedAtState: 'BranchCreated', errorCode: 'PublicationFileFailed', writesPossible: true, pullNumber: null, pullUrl: null, commitSha: null });
  await setup(page, approved(), { publication: enabled, publishRefuse: { status: 422, body: { error: 'PublicationFileFailed', details: [], publication: failed } }, attempts: [failed] });
  await open(page);
  await propose(page);
  await repositoryField(page).fill('orion-api');
  await plan(page).click();
  await confirmBox(page).check();
  await publishButton(page).click();
  await expect(page.getByRole('alert')).toContainText(/PublicationFileFailed/);
  await expect(panel(page).getByText(/potrebbe esistere qualcosa|something may exist/i).first()).toBeVisible();
  await expect(panel(page).getByText(/branch creato|branch created/i).first()).toBeVisible();
});

test('a link to anything but http(s) is not rendered as a link', async ({ page }) => {
  await setup(page, approved(), { publication: enabled, published: publicationRecord({ pullUrl: 'javascript:alert(1)' }) });
  await open(page);
  await propose(page);
  await repositoryField(page).fill('orion-api');
  await plan(page).click();
  await confirmBox(page).check();
  await publishButton(page).click();
  await expect(panel(page).getByText(/pull request #7 aperta|pull request #7 opened/i)).toBeVisible();
  await expect(panel(page).getByRole('link', { name: /aprila su github|open it on github/i })).toHaveCount(0);
});
