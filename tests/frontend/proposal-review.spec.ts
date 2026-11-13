import { expect, test, type Page } from '@playwright/test';

const sourceId = '11111111-1111-1111-1111-111111111111';
const aiId = '5d3b7b4e-3c6a-4d0e-9f53-0b1a8f6d9a11';
const proposalId = '7a1c2d3e-4b5f-4a6b-8c7d-9e0f1a2b3c4d';
const sha = 'ab'.repeat(32);

type Detail = Record<string, any>;

function detail(overrides: Detail = {}): Detail {
  return {
    id: proposalId, state: 'Proposed', reason: null, sourceConnectionId: sourceId, projectId: 'project-guid', pipelineId: 123, aiConnectionId: aiId,
    workflowYaml: '# azure-tasks: converted=UseDotNet@2 manual=none\nname: demo\non:\n  workflow_dispatch:\njobs: {}\n', workflowSha256: sha, modelNotes: 'Triggers are not converted.',
    approvedSha256: null, integrityOk: true, createdBy: 'author', createdAtUtc: '2026-11-13T12:00:00Z', createdByMe: false, requiresSecondApprover: true, canApprove: true,
    gates: [{ name: 'structure', status: 'Passed', detail: null }, { name: 'zizmor', status: 'Passed', detail: null }],
    findings: [
      { gate: 'policy', code: 'ACTION_PIN_NOT_VERIFIED', severity: 'Warning', message: 'pin not verified', line: 9 },
      { gate: 'zizmor', code: 'ZIZMOR_SECRETS_OUTSIDE_ENV', severity: 'Warning', message: 'secrets outside an environment', line: 12 }
    ],
    plan: { secrets: [{ name: 'DEPLOY_PASSWORD', origin: 'pipeline variable' }], variables: [], connections: [], notes: [] },
    provenance: null,
    events: [{ id: 1, type: 'Created', actor: 'author', occurredAtUtc: '2026-11-13T12:00:00Z', workflowSha256: sha, note: null, reportSha256: null, policyVersion: null, acknowledgedWarnings: [] }],
    ...overrides
  };
}

function convertAnswer(d: Detail) {
  return {
    status: 'ReadyForReview', reason: null, workflowYaml: d.workflowYaml, workflowSha256: d.workflowSha256, modelNotes: d.modelNotes, approvable: true, proposalOnly: true,
    saved: true, proposalId: d.id, proposalState: d.state, gates: d.gates, findings: d.findings, plan: d.plan, provenance: null
  };
}

// An API that behaves like the real one for the parts the screen depends on, so the tests read the requests it receives.
async function setup(page: Page, initial: Detail, options: { history?: Detail[]; refuse?: { status: number; body: Record<string, unknown> }; listFails?: boolean } = {}) {
  const store = { current: initial, kept: options.history ?? [], posts: [] as { verb: string; body: Record<string, any> }[] };
  const summaries = () => [store.current, ...store.kept.filter(k => k.id !== store.current.id)].map(d => ({
    id: d.id, state: d.state, reason: d.reason, workflowSha256: d.workflowSha256, createdBy: d.createdBy, createdAtUtc: d.createdAtUtc }));

  await page.route('**/api/auth/login', route => route.fulfill({ json: { accessToken: 't', mustChangePassword: false, username: 'reviewer', role: 'superadmin' } }));
  await page.route('**/api/connections', route => route.fulfill({ json: [
    { id: sourceId, providerKey: 'azure-devops-services', direction: 'source', name: 'DevOps', organization: 'source', validationCode: 'Verified' }] }));
  await page.route('**/api/ai-connections', route => route.fulfill({ json: [{
    id: aiId, name: 'Corporate model', provider: 'OpenAICompatible', endpoint: 'https://llm.corp.example/v1', model: 'gpt-4.1', auth: 'ApiKey', verificationCode: 'Verified', sendSourceContent: true }] }));
  await page.route('**/api/workloads/pipelines/convert/status', route => route.fulfill({ json: { actionlint: true, zizmor: true, canBeApprovable: true, policyVersion: 'x', actionCatalogVerifiedOn: 'y' } }));
  await page.route('**/api/workloads/pipelines/convert', route => route.fulfill({ json: convertAnswer(store.current) }));
  await page.route(/\/api\/workloads\/pipelines\/proposals/, async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (request.method() === 'GET' && path.endsWith('/proposals')) {
      return options.listFails ? route.fulfill({ status: 500, json: { error: 'x' } }) : route.fulfill({ json: summaries() });
    }
    const id = /proposals\/([0-9a-f-]{36})/.exec(path)?.[1];
    const target = [store.current, ...store.kept].find(d => d.id === id);
    if (!target) return route.fulfill({ status: 404, json: {} });
    if (request.method() === 'GET') return route.fulfill({ json: target });
    const verb = path.split('/').pop()!;
    const body = request.postDataJSON() as Record<string, any>;
    store.posts.push({ verb, body });
    if (options.refuse) return route.fulfill({ status: options.refuse.status, json: options.refuse.body });
    const type = verb === 'approve' ? 'Approved' : verb === 'reject' ? 'Rejected' : 'Revoked';
    const next = {
      ...target, state: type === 'Approved' ? 'Approved' : type, approvedSha256: type === 'Rejected' ? null : sha, canApprove: false,
      events: [...target.events, { id: target.events.length + 1, type, actor: 'reviewer', occurredAtUtc: '2026-11-13T13:00:00Z', workflowSha256: sha, note: body.note || null,
        reportSha256: type === 'Approved' ? 'c'.repeat(64) : null, policyVersion: type === 'Approved' ? '2026-11-1' : null, acknowledgedWarnings: body.acknowledgedWarnings ?? [] }]
    };
    if (target === store.current) store.current = next; else store.kept = store.kept.map(k => k.id === next.id ? next : k);
    return route.fulfill({ json: next });
  });
  await page.goto('/');
  await page.getByRole('textbox', { name: /nome utente|username/i }).fill('reviewer');
  await page.getByLabel(/^password$/i).fill('sample-password');
  await page.getByRole('button', { name: /^accedi$|^sign in$/i }).click();
  await page.getByRole('button', { name: /Pipelines.*Actions/ }).click();
  await page.getByRole('textbox', { name: /ID progetto Azure|Azure project ID/i }).fill('project-guid');
  await page.getByRole('spinbutton', { name: /ID pipeline Azure|Azure pipeline ID/i }).fill('123');
  return store;
}

const open = (page: Page) => page.getByRole('button', { name: /proponi una conversione con ai|propose a conversion with ai/i }).click();
const propose = (page: Page) => page.getByRole('button', { name: /proponi un workflow|propose a workflow/i }).click();
const approve = (page: Page) => page.getByRole('button', { name: /approva esattamente questo testo|approve this exact text/i });
const reject = (page: Page) => page.getByRole('button', { name: /^rifiuta$|^reject$/i });
const revoke = (page: Page) => page.getByRole('button', { name: /revoca l.approvazione|revoke the approval/i });
const note = (page: Page) => page.getByRole('textbox', { name: /nota \(obbligatoria|note \(required/i });
const review = (page: Page) => page.getByLabel(/revisione umana|human review/i);

test('a kept proposal is approved only after every warning is accepted, and the request names the text and the warnings', async ({ page }) => {
  const store = await setup(page, detail());
  await open(page);
  await propose(page);

  await expect(review(page)).toBeVisible();
  await expect(review(page).getByText(sha)).toBeVisible();
  await expect(page.getByText(/in attesa della decisione|waiting for a person to decide/i).first()).toBeVisible();
  await expect(approve(page)).toBeDisabled();
  await page.getByRole('checkbox', { name: /ACTION_PIN_NOT_VERIFIED/ }).check();
  await expect(approve(page)).toBeDisabled(); // one of two is not enough
  await page.getByRole('checkbox', { name: /ZIZMOR_SECRETS_OUTSIDE_ENV/ }).check();
  await expect(approve(page)).toBeEnabled();
  await note(page).fill('Read it line by line.');
  await approve(page).click();

  await expect(page.getByText(/approvata da una persona|approved by a person/i).first()).toBeVisible();
  await expect(page.getByText(/approvata da reviewer|approved by reviewer/i)).toBeVisible();
  await expect(page.getByText(/approvato non è pubblicato|approved is not published/i)).toBeVisible();
  await expect(approve(page)).toHaveCount(0);
  await expect(revoke(page)).toBeVisible();
  expect(store.posts).toEqual([{ verb: 'approve', body: { workflowSha256: sha, acknowledgedWarnings: ['ACTION_PIN_NOT_VERIFIED', 'ZIZMOR_SECRETS_OUTSIDE_ENV'], note: 'Read it line by line.' } }]);
});

test('whoever asked for the workflow cannot approve it, and rejecting needs a reason', async ({ page }) => {
  const store = await setup(page, detail({ createdByMe: true, canApprove: false }));
  await open(page);
  await propose(page);

  await expect(page.getByText(/un.altra persona|another person has to approve/i)).toBeVisible();
  await expect(approve(page)).toHaveCount(0);
  await expect(page.getByRole('checkbox')).toHaveCount(0);
  await expect(reject(page)).toBeDisabled();
  await note(page).fill('no');
  await expect(reject(page)).toBeDisabled();
  await note(page).fill('uses a deploy step we do not want');
  await expect(reject(page)).toBeEnabled();
  await reject(page).click();
  await expect(page.getByText(/rifiutata da una persona|rejected by a person/i).first()).toBeVisible();
  expect(store.posts).toEqual([{ verb: 'reject', body: { note: 'uses a deploy step we do not want' } }]);
});

test('a warning the fresh run found is asked for by name, and a refusal is explained without losing the form', async ({ page }) => {
  await setup(page, detail({ findings: [{ gate: 'policy', code: 'ACTION_PIN_NOT_VERIFIED', severity: 'Warning', message: 'pin not verified', line: 9 }] }), {
    refuse: { status: 422, body: { error: 'WarningsNotAcknowledged', details: ['ZIZMOR_NEW_FINDING'] } }
  });
  await open(page);
  await propose(page);
  await page.getByRole('checkbox', { name: /ACTION_PIN_NOT_VERIFIED/ }).check();
  await approve(page).click();

  await expect(page.getByRole('alert')).toContainText(/ZIZMOR_NEW_FINDING/);
  await expect(page.getByRole('alert')).toContainText(/accetta gli avvisi|accept the warnings/i);
  const late = page.getByRole('checkbox', { name: /ZIZMOR_NEW_FINDING/ });
  await expect(late).toBeVisible();
  await expect(approve(page)).toBeDisabled(); // the new one is not accepted yet
  await expect(page.getByRole('checkbox', { name: /ACTION_PIN_NOT_VERIFIED/ })).toBeChecked();
  await late.check();
  await expect(approve(page)).toBeEnabled();
});

test('a refusal that cannot be fixed on the screen is reported and the proposal stays undecided', async ({ page }) => {
  await setup(page, detail({ findings: [] }), { refuse: { status: 422, body: { error: 'GatesNoLongerPass', details: ['actionlint:NotRun'] } } });
  await open(page);
  await propose(page);
  await approve(page).click();
  await expect(page.getByRole('alert')).toContainText(/rieseguiti ora|run again now/i);
  await expect(page.getByRole('alert')).toContainText('actionlint:NotRun');
  await expect(approve(page)).toBeVisible();
  await expect(page.getByText(/approvata da una persona|approved by a person/i)).toHaveCount(0);
});

test('kept proposals are listed, opened and revoked with a reason, and the approval stays on record', async ({ page }) => {
  const approved = detail({
    state: 'Approved', approvedSha256: sha, canApprove: false, findings: [],
    events: [
      { id: 1, type: 'Created', actor: 'author', occurredAtUtc: '2026-11-13T12:00:00Z', workflowSha256: sha, note: null, reportSha256: null, policyVersion: null, acknowledgedWarnings: [] },
      { id: 2, type: 'Approved', actor: 'second-person', occurredAtUtc: '2026-11-13T12:30:00Z', workflowSha256: sha, note: 'checked', reportSha256: 'c'.repeat(64), policyVersion: '2026-11-1', acknowledgedWarnings: ['ZIZMOR_SECRETS_OUTSIDE_ENV'] }
    ]
  });
  const store = await setup(page, approved);
  await open(page);
  await expect(page.getByText(/proposte conservate per questa pipeline|kept proposals for this pipeline/i)).toBeVisible();
  await page.getByRole('button', { name: /^apri$|^open$/i }).click();

  await expect(page.getByText(/approvata da second-person|approved by second-person/i)).toBeVisible();
  await expect(page.getByText(/ZIZMOR_SECRETS_OUTSIDE_ENV/)).toBeVisible(); // what was accepted is shown
  await expect(revoke(page)).toBeDisabled();
  await note(page).fill('found a problem in review');
  await revoke(page).click();
  await expect(page.getByText(/approvazione revocata|approval revoked/i).first()).toBeVisible();
  await expect(revoke(page)).toHaveCount(0);
  // the first approval is still in the history
  await expect(page.getByText(/second-person/).first()).toBeVisible();
  expect(store.posts).toEqual([{ verb: 'revoke', body: { note: 'found a problem in review' } }]);
});

test('a blocked proposal in the list is opened without any decision to make, and a kept text that no longer matches its hash cannot be approved', async ({ page }) => {
  const blocked = detail({ id: '8b2d3e4f-5c6a-4b7c-9d8e-0f1a2b3c4d5e', state: 'Blocked', reason: 'GatesBlocked', workflowYaml: null, canApprove: false,
    findings: [{ gate: 'policy', code: 'REMOTE_SCRIPT_EXECUTION', severity: 'Blocker', message: 'A downloaded script is executed without verification.', line: 14 }] });
  await setup(page, detail({ integrityOk: false }), { history: [blocked] });
  await open(page);
  await propose(page);
  await expect(page.getByRole('alert')).toContainText(/non corrisponde più al suo hash|no longer matches its hash/i);
  await expect(approve(page)).toBeDisabled();

  await page.getByRole('button', { name: /^apri$|^open$/i }).nth(1).click();
  await expect(page.getByText('REMOTE_SCRIPT_EXECUTION')).toBeVisible();
  await expect(review(page)).toHaveCount(0);
  await expect(page.getByRole('button', { name: /^copia|^copy/i })).toHaveCount(0);
});

test('when the kept proposals cannot be loaded the panel says so quietly and still proposes', async ({ page }) => {
  await setup(page, detail(), { listFails: true });
  await open(page);
  await expect(page.getByText(/non è stato possibile caricare le proposte|could not be loaded/i)).toBeVisible();
  await expect(page.getByRole('alert')).toHaveCount(0);
  await propose(page);
  await expect(review(page)).toBeVisible();
});
