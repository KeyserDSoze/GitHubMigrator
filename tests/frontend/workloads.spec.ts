import { expect, test, type Page } from '@playwright/test';

const sourceId = '11111111-1111-1111-1111-111111111111';
const destId = '22222222-2222-2222-2222-222222222222';

async function login(page: Page, role: string) {
  await page.route('**/api/auth/login', route => route.fulfill({ json: {
    accessToken: 'test-token', mustChangePassword: false, username: role, role
  } }));
  await page.goto('/');
  await page.getByRole('textbox', { name: /nome utente|username/i }).fill(role);
  await page.getByLabel(/^password$/i).fill('sample-password');
  await page.getByRole('button', { name: /^accedi$|^sign in$/i }).click();
}
function connections(page: Page) {
  return page.route('**/api/connections', route => route.fulfill({ json: [
    { id: sourceId, providerKey: 'azure-devops-services', direction: 'source', name: 'DevOps', organization: 'source',
      validationCode: 'Verified' },
    { id: destId, providerKey: 'github-enterprise-cloud', direction: 'destination', name: 'GitHub', organization: 'target',
      validationCode: 'Verified' }
  ] }));
}

test('admin previews disabled Actions scaffold, AI status and exact configuration steps', async ({ page }) => {
  await connections(page);
  let previewCount = 0;
  await page.route('**/api/workloads/pipelines/preview', route => {
    previewCount++;
    expect(route.request().postDataJSON()).toMatchObject({
      sourceConnectionId: sourceId, destinationConnectionId: destId,
      projectId: 'project-guid', pipelineId: 123, targetRepository: 'target-repo'
    });
    return route.fulfill({ json: {
      pipelineId: 123, sourceName: 'Build App', targetRepository: 'target-repo',
      workflowPath: '.github/workflows/azdo-123-build-app.yml',
      workflowYaml: '# disabled review only\non:\n  workflow_dispatch:\njobs: {}\n',
      setupSteps: [{ name: 'AZURE_CLIENT_ID', kind: 'secret', existsOnGitHub: false,
        instructions: 'Settings > Secrets and variables > Actions > Add repository secret' }],
      ai: { status: 'not-configured', findings: ['Configure Foundry before AI review.'] },
      issues: [{ code: 'WorkflowConversionRequiresReview', detail: 'Human review required.', severity: 'blocker' }],
      deployableWithoutReview: false
    } });
  });
  await login(page, 'admin');
  await page.getByRole('button', { name: /Pipelines.*Actions/ }).click();
  await page.getByRole('textbox', { name: /ID progetto Azure|Azure project ID/i }).fill('project-guid');
  await page.getByRole('textbox', { name: /Repository GitHub|GitHub repository/i }).fill('target-repo');
  await page.getByRole('spinbutton', { name: /ID pipeline Azure|Azure pipeline ID/i }).fill('123');
  await page.getByRole('button', { name: /Genera anteprima|Generate preview/i }).click();
  await expect(page.getByText('Build App', { exact: true })).toBeVisible();
  await expect(page.getByText('AZURE_CLIENT_ID')).toBeVisible();
  await expect(page.getByText('WorkflowConversionRequiresReview')).toBeVisible();
  await expect(page.getByText('not-configured')).toBeVisible();
  expect(previewCount).toBe(1);
});

test('admin previews source IDs and unresolved Board/commit link warnings', async ({ page }) => {
  await connections(page);
  await page.route('**/api/workloads/boards/preview', route => {
    expect(route.request().postDataJSON()).toMatchObject({
      sourceConnectionId: sourceId, projectId: 'project-guid', workItemIds: [101, 102],
      knownGitHubIssues: { 102: 55 }
    });
    return route.fulfill({ json: {
      issues: [{ sourceId: 101, title: 'Fix login', state: 'Active',
        body: '<!-- azdo-workitem:source:project-guid:101 -->\n**Azure DevOps ID:** 101\n- linked #55',
        issues: [{ code: 'CommitTargetUnverified', detail: 'Commit needs GitHub verification', severity: 'warning' }] }],
      globalWarnings: [{ code: 'TraceabilityIncomplete', detail: 'Link verification pending', severity: 'blocker' }],
      readyToPublish: false
    } });
  });
  await login(page, 'admin');
  await page.getByRole('button', { name: /Boards.*Issues/ }).click();
  await page.getByRole('textbox', { name: /ID progetto Azure|Azure project ID/i }).fill('project-guid');
  await page.getByRole('textbox', { name: /ID work item Azure|Azure work item IDs/i }).fill('101,102');
  await page.getByRole('textbox', { name: /Numeri issue GitHub|Existing GitHub issue numbers/i }).fill('102:55');
  await page.getByRole('button', { name: /Genera anteprima|Generate preview/i }).click();
  await expect(page.getByText(/101.*Fix login/)).toBeVisible();
  await expect(page.getByText('TraceabilityIncomplete')).toBeVisible();
  await expect(page.getByText(/CommitTargetUnverified/)).toBeVisible();
});

test('regular user cannot query migration previews', async ({ page }) => {
  let apiCalls = 0;
  await page.route('**/api/connections', route => { apiCalls++; return route.fulfill({ status: 403 }); });
  await page.route('**/api/workloads/**', route => { apiCalls++; return route.fulfill({ status: 403 }); });
  await login(page, 'user');
  await page.getByRole('button', { name: /Pipelines.*Actions/ }).click();
  await expect(page.getByText(/Solo gli amministratori|Only administrators/i)).toBeVisible();
  await page.getByRole('button', { name: /Boards.*Issues/ }).click();
  await expect(page.getByText(/Solo gli amministratori|Only administrators/i)).toBeVisible();
  expect(apiCalls).toBe(0);
});
