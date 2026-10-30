import { expect, test, type Page } from '@playwright/test';

const src = '11111111-1111-1111-1111-111111111111';
const dest = '22222222-2222-2222-2222-222222222222';
const sha = 'aaaabbbbccccddddeeeeffff0000111122223333';

async function enter(page: Page) {
  await page.route('**/api/auth/login', route => route.fulfill({ json: {
    accessToken: 'session-test-only', mustChangePassword: false, username: 'admin', role: 'admin'
  } }));
  await page.route('**/api/connections', route => route.fulfill({ json: [
    { id: src, providerKey: 'azure-devops-services', direction: 'source',
      name: 'Fixture Azure DevOps', organization: 'sample', validationCode: 'Verified' },
    { id: dest, providerKey: 'github-enterprise-cloud', direction: 'destination',
      name: 'Fixture GitHub', organization: 'target', validationCode: 'Verified' }
  ] }));
  await page.goto('/');
  await page.getByRole('textbox', { name: /nome utente|username/i }).fill('admin');
  await page.getByLabel(/^password$/i).fill('password-test');
  await page.getByRole('button', { name: /^accedi$|^sign in$/i }).click();
}

async function openBoard(page: Page) {
  await page.getByRole('button', { name: /Boards.*Issues/ }).click();
  await page.getByRole('textbox', { name: /ID progetto Azure|Azure project ID/i }).fill('project');
  await page.getByRole('textbox', { name: /Repository GitHub|GitHub repository/i }).fill('migration-target');
  await page.getByRole('textbox', { name: /ID work item Azure|Azure work item IDs/i }).fill('101,102');
}

test('Board reconciliation passes mappings and displays target existence separately from relationship completion', async ({ page }) => {
  let calls = 0;
  await page.route('**/api/workloads/boards/reconcile', route => {
    calls++;
    expect(route.request().method()).toBe('POST');
    expect(route.request().postDataJSON()).toMatchObject({
      sourceConnectionId: src, destinationConnectionId: dest,
      projectId: 'project', targetRepository: 'migration-target',
      workItemIds: [101, 102], knownGitHubIssues: { 102: 88 },
      knownGitHubPullRequests: { 301: 22 },
      knownGitHubCommits: { [sha]: sha }
    });
    return route.fulfill({ json: {
      targetOrganization: 'target', targetRepository: 'migration-target',
      links: [
        { workItemId: 101, sourceRelation: 'System.LinkTypes.Hierarchy-Forward',
          sourceReference: '102', targetReference: '#88', status: 'targetExists' },
        { workItemId: 101, sourceRelation: 'commit',
          sourceReference: sha, targetReference: sha, status: 'targetExists' },
        { workItemId: 102, sourceRelation: 'pull-request',
          sourceReference: '301', targetReference: '#22', status: 'notFound' }
      ],
      targetObjectsFound: 2, unresolvedLinks: 1, readyToPublish: false,
      issues: [{ code: 'UnresolvedTraceability', detail: 'One source link not verified.', severity: 'blocker' }]
    } });
  });
  await enter(page);
  await openBoard(page);
  await page.getByRole('textbox', { name: /Numeri issue GitHub|Existing GitHub issue numbers/i }).fill('102:88');
  await page.getByRole('textbox', { name: /Mappatura SHA|Commit SHA mappings/i }).fill(sha + ':' + sha);
  await page.getByRole('textbox', { name: /Mappatura pull request|Pull request mappings/i }).fill('301:22');
  await page.getByRole('button', { name: /Verifica i collegamenti target|Check target links/i }).click();
  await expect(page.getByRole('heading', { name: /Verifica tracciabilità|Source-to-GitHub traceability/i })).toBeVisible();
  await expect(page.getByText('UnresolvedTraceability')).toBeVisible();
  await expect(page.getByText('targetExists').first()).toBeVisible();
  await expect(page.getByText('notFound')).toBeVisible();
  await expect(page.getByText(/Trovare un oggetto GitHub|Finding the target object/i)).toBeVisible();
  expect(calls).toBe(1);
});

test('Board reconciliation rejects malformed SHA mapping before any HTTP request', async ({ page }) => {
  let calls = 0;
  await page.route('**/api/workloads/boards/reconcile', route => {
    calls++; return route.fulfill({ status: 500 });
  });
  await enter(page);
  await openBoard(page);
  await page.getByRole('textbox', { name: /Mappatura SHA|Commit SHA mappings/i }).fill('123:malformed');
  await page.getByRole('button', { name: /Verifica i collegamenti target|Check target links/i }).click();
  await expect(page.getByRole('alert').getByText(/Anteprima non riuscita|Preview failed/i)).toBeVisible();
  expect(calls).toBe(0);
});

test('Pipeline preview displays provider access errors rather than success', async ({ page }) => {
  await enter(page);
  await page.route('**/api/workloads/pipelines/preview', route =>
    route.fulfill({ status: 422, json: { error: 'ProviderReadFailed' } }));
  await page.getByRole('button', { name: /Pipelines.*Actions/ }).click();
  await page.getByRole('textbox', { name: /ID progetto Azure|Azure project ID/i }).fill('project');
  await page.getByRole('textbox', { name: /Repository GitHub|GitHub repository/i }).fill('repo');
  await page.getByRole('spinbutton', { name: /ID pipeline Azure|Azure pipeline ID/i }).fill('7');
  await page.getByRole('button', { name: /Genera anteprima|Generate preview/i }).click();
  await expect(page.getByRole('alert').getByText(/Anteprima non riuscita|Preview failed/i)).toBeVisible();
  await expect(page.getByText(/Bozza workflow disabilitata|Disabled workflow scaffold/i)).toHaveCount(0);
});

test('Board preview rejects duplicate work item IDs without calling provider', async ({ page }) => {
  let calls = 0;
  await page.route('**/api/workloads/boards/preview', route => {
    calls++; return route.fulfill({ status: 500 });
  });
  await enter(page);
  await openBoard(page);
  await page.getByRole('textbox', { name: /ID work item Azure|Azure work item IDs/i }).fill('101,101');
  await page.getByRole('button', { name: /Genera anteprima|Generate preview/i }).click();
  await expect(page.getByRole('alert').getByText(/Anteprima non riuscita|Preview failed/i)).toBeVisible();
  expect(calls).toBe(0);
});
