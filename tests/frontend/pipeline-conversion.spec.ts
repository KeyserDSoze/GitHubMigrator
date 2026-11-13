import { expect, test, type Page } from '@playwright/test';

const sourceId = '11111111-1111-1111-1111-111111111111';
const destId = '22222222-2222-2222-2222-222222222222';
const goodAi = '5d3b7b4e-3c6a-4d0e-9f53-0b1a8f6d9a11';
const noConsentAi = '6e4c8c5f-4d7b-4e1f-8a64-1c2b9a7e0b22';

function profile(id: string, name: string, overrides: Record<string, unknown> = {}) {
  return {
    id, name, provider: 'OpenAICompatible', endpoint: 'https://llm.corp.example/v1', model: 'gpt-4.1', auth: 'ApiKey', apiVersion: null,
    tokenParameter: 'MaxTokens', maxOutputTokens: 4096, temperature: 0, timeoutSeconds: 60, maxInputBytes: 200000, createdAtUtc: '2026-11-13T09:30:00Z',
    verifiedAtUtc: '2026-11-13T09:30:00Z', verificationCode: 'Verified', verifiedModel: 'gpt-4.1', sendSourceContent: true, consentAtUtc: '2026-11-13T10:00:00Z',
    consentBy: 'superadmin', ...overrides
  };
}

function proposal(overrides: Record<string, unknown> = {}) {
  return {
    status: 'ReadyForReview', reason: null, workflowYaml: '# azure-tasks: converted=UseDotNet@2 manual=none\nname: demo\non:\n  workflow_dispatch:\njobs: {}\n',
    workflowSha256: 'a'.repeat(64), modelNotes: 'Triggers are not converted.', approvable: true, proposalOnly: true, saved: false, proposalId: null, proposalState: null,
    gates: [{ name: 'structure', status: 'Passed', detail: null }, { name: 'policy', status: 'Passed', detail: null },
      { name: 'actionlint', status: 'Passed', detail: null }, { name: 'zizmor', status: 'Passed', detail: null }],
    findings: [{ gate: 'policy', code: 'ACTION_PIN_NOT_VERIFIED', severity: 'Warning', message: 'pin not verified', line: 9 }],
    plan: { secrets: [{ name: 'DEPLOY_PASSWORD', origin: 'pipeline variable' }], variables: [{ name: 'BUILD_REGION', origin: "variable group 'prod'" }], connections: [], notes: [] },
    provenance: {
      aiConnectionId: goodAi, provider: 'OpenAICompatible', host: 'llm.corp.example', model: 'gpt-4.1', reportedModel: 'gpt-4.1-2025', promptSha256: 'b'.repeat(64),
      planSha256: 'c'.repeat(64), policyVersion: '2026-11-1', actionCatalogVerifiedOn: '2026-11-13', redactions: [{ kind: 'SENSITIVE_LITERAL', count: 2 }],
      totalPromptTokens: 1200, totalCompletionTokens: 340, attempts: [{ number: 1, outcome: 'GatesPassed', promptTokens: 1200, completionTokens: 340, workflowSha256: 'a'.repeat(64), blockerCodes: [] }],
      generatedAtUtc: '2026-11-13T12:00:00Z'
    },
    ...overrides
  };
}

async function setup(page: Page, convert: (body: Record<string, unknown>) => Record<string, unknown>, analysers = true) {
  await page.route('**/api/auth/login', route => route.fulfill({ json: { accessToken: 'test-token', mustChangePassword: false, username: 'admin', role: 'admin' } }));
  await page.route('**/api/connections', route => route.fulfill({ json: [
    { id: sourceId, providerKey: 'azure-devops-services', direction: 'source', name: 'DevOps', organization: 'source', validationCode: 'Verified' },
    { id: destId, providerKey: 'github-enterprise-cloud', direction: 'destination', name: 'GitHub', organization: 'target', validationCode: 'Verified' }
  ] }));
  await page.route('**/api/ai-connections', route => route.fulfill({ json: [profile(goodAi, 'Corporate model'), profile(noConsentAi, 'Unconsented', { sendSourceContent: false })] }));
  // the kept-proposals list is a convenience of the panel; these tests are about the proposal itself
  await page.route(/\/api\/workloads\/pipelines\/proposals/, route => route.fulfill({ json: [] }));
  await page.route('**/api/workloads/pipelines/convert/status', route => route.fulfill({ json: {
    actionlint: analysers, zizmor: analysers, canBeApprovable: analysers, policyVersion: '2026-11-1', actionCatalogVerifiedOn: '2026-11-13' } }));
  await page.route('**/api/workloads/pipelines/convert', async route => {
    await route.fulfill({ json: convert(route.request().postDataJSON() as Record<string, unknown>) });
  });
  await page.goto('/');
  await page.getByRole('textbox', { name: /nome utente|username/i }).fill('admin');
  await page.getByLabel(/^password$/i).fill('sample-password');
  await page.getByRole('button', { name: /^accedi$|^sign in$/i }).click();
  await page.getByRole('button', { name: /Pipelines.*Actions/ }).click();
  await page.getByRole('textbox', { name: /ID progetto Azure|Azure project ID/i }).fill('project-guid');
  await page.getByRole('spinbutton', { name: /ID pipeline Azure|Azure pipeline ID/i }).fill('123');
}

const open = (page: Page) => page.getByRole('button', { name: /proponi una conversione con ai|propose a conversion with ai/i }).click();
const propose = (page: Page) => page.getByRole('button', { name: /proponi un workflow|propose a workflow/i }).click();

test('the conversion panel loads nothing until it is opened and then only proposes, with the evidence', async ({ page }) => {
  let listed = 0;
  await page.route('**/api/ai-connections', route => { listed++; return route.fulfill({ json: [profile(goodAi, 'Corporate model'), profile(noConsentAi, 'Unconsented', { sendSourceContent: false })] }); });
  const sent: Record<string, unknown>[] = [];
  await setup(page, body => { sent.push(body); return proposal(); });
  expect(listed).toBe(0);
  await open(page);
  await expect(page.getByText(/solo una proposta|a proposal only/i)).toBeVisible();
  await expect(page.getByLabel(/profilo ai|ai profile/i)).toHaveValue(goodAi);
  await propose(page);

  await expect(page.getByRole('status')).toContainText(/una persona può rivederla|a person may now review it/i);
  await expect(page.getByText(/non è un.approvazione|is not approval/i)).toBeVisible();
  await expect(page.getByText(/azure-tasks: converted=UseDotNet@2/)).toBeVisible();
  await expect(page.getByText('structure: ', { exact: false })).toBeVisible();
  await expect(page.getByText(/GatesPassed/)).toBeVisible();
  await expect(page.getByText(/SENSITIVE_LITERAL × 2/)).toBeVisible();
  await expect(page.getByText(/DEPLOY_PASSWORD/)).toBeVisible();
  await expect(page.getByText(/BUILD_REGION/)).toBeVisible();
  await expect(page.getByText('aaaaaaaaaaaa')).toBeVisible();
  // a request carries identifiers only: never source text, a key or a value
  expect(sent).toEqual([{ sourceConnectionId: sourceId, projectId: 'project-guid', pipelineId: 123, aiConnectionId: goodAi }]);
});

test('a blocked proposal lists what blocks it and is never presented as ready', async ({ page }) => {
  await setup(page, () => proposal({
    status: 'Blocked', reason: 'GatesBlocked', approvable: false,
    gates: [{ name: 'policy', status: 'Failed', detail: null }, { name: 'actionlint', status: 'NotRun', detail: 'actionlint is not installed or configured.' }],
    findings: [{ gate: 'policy', code: 'REMOTE_SCRIPT_EXECUTION', severity: 'Blocker', message: 'A downloaded script is executed without verification.', line: 14 }]
  }));
  await open(page);
  await propose(page);
  await expect(page.getByRole('status')).toContainText(/bloccata dai gate|blocked by the gates/i);
  await expect(page.getByText('REMOTE_SCRIPT_EXECUTION')).toBeVisible();
  await expect(page.getByText(/riga 14|line 14/i)).toBeVisible();
  await expect(page.getByText(/policy: fallito|policy: failed/i)).toBeVisible();
  await expect(page.getByText(/actionlint: non eseguito|actionlint: did not run/i)).toBeVisible();
  await expect(page.getByText(/ora una persona può rivederla|a person may now review it/i)).toHaveCount(0);
  await expect(page.getByText(/così non può essere approvata|cannot be approved as it is/i)).toBeVisible();
});

test('without consent nothing is proposed and the reason is explained, and profiles that may not send are not selectable', async ({ page }) => {
  await setup(page, () => proposal({ status: 'NotAttempted', reason: 'SourceConsentRequired', workflowYaml: null, workflowSha256: null, modelNotes: null, approvable: false, gates: [], findings: [], provenance: null }));
  await open(page);
  const option = page.getByRole('option', { name: /Unconsented/ });
  await expect(option).toHaveAttribute('disabled', '');
  await expect(option).toContainText(/invio del sorgente non consentito|source sending not allowed/i);
  await propose(page);
  await expect(page.getByRole('status')).toContainText(/nessuna proposta|no proposal/i);
  await expect(page.getByText(/non è stato consentito da un superadmin|has not been allowed by a superadmin/i)).toBeVisible();
  await expect(page.getByRole('button', { name: /^copia|^copy/i })).toHaveCount(0);
});

test('a model failure is explained with its code and missing analysers are announced up front', async ({ page }) => {
  await setup(page, () => proposal({ status: 'NotAttempted', reason: 'ModelFailure:RateLimited', workflowYaml: null, workflowSha256: null, modelNotes: null, approvable: false, gates: [], findings: [] }), false);
  await open(page);
  await expect(page.getByText(/actionlint, zizmor/)).toBeVisible();
  await expect(page.getByText(/senza non può esserci una proposta approvabile|cannot be approvable without them/i)).toBeVisible();
  await propose(page);
  await expect(page.getByText(/RateLimited/)).toBeVisible();
});

test('a failing request shows an error and leaves no stale proposal', async ({ page }) => {
  await setup(page, () => proposal());
  await page.route('**/api/workloads/pipelines/convert', route => route.fulfill({ status: 400, json: { error: 'InvalidProjectId' } }));
  await open(page);
  await propose(page);
  await expect(page.getByRole('alert')).toContainText(/richiesta di conversione non è riuscita|conversion request failed/i);
  await expect(page.getByText(/azure-tasks/)).toHaveCount(0);
});
