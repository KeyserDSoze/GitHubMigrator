import { type Page } from '@playwright/test';

export const sourceId = '11111111-1111-1111-1111-111111111111';
export const destinationId = '22222222-2222-2222-2222-222222222222';
export const aiId = '5d3b7b4e-3c6a-4d0e-9f53-0b1a8f6d9a11';
export const proposalId = '7a1c2d3e-4b5f-4a6b-8c7d-9e0f1a2b3c4d';
export const sha = 'ab'.repeat(32);

export type Detail = Record<string, any>;

export function detail(overrides: Detail = {}): Detail {
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

export function convertAnswer(d: Detail) {
  return {
    status: 'ReadyForReview', reason: null, workflowYaml: d.workflowYaml, workflowSha256: d.workflowSha256, modelNotes: d.modelNotes, approvable: true, proposalOnly: true,
    saved: true, proposalId: d.id, proposalState: d.state, gates: d.gates, findings: d.findings, plan: d.plan, provenance: null
  };
}

// An API that behaves like the real one for the parts the screen depends on, so the tests read the requests it receives.
export async function setup(page: Page, initial: Detail, options: { history?: Detail[]; refuse?: { status: number; body: Record<string, unknown> }; listFails?: boolean;
  plan?: Detail; planRefuse?: { status: number; body: Record<string, unknown> };
  role?: string; publication?: { enabled: boolean; allowed: string[] }; attempts?: Detail[]; published?: Detail; publishRefuse?: { status: number; body: Record<string, unknown> };
  derived?: Detail; deriveRefuse?: { status: number; body: Record<string, unknown> } } = {}) {
  const store = { current: initial, kept: options.history ?? [], posts: [] as { verb: string; body: Record<string, any> }[], plans: [] as Record<string, any>[], publishes: [] as Record<string, any>[], attempts: [...(options.attempts ?? [])], derives: [] as string[] };
  const summaries = () => [store.current, ...store.kept.filter(k => k.id !== store.current.id)].map(d => ({
    id: d.id, state: d.state, reason: d.reason, workflowSha256: d.workflowSha256, createdBy: d.createdBy, createdAtUtc: d.createdAtUtc, kind: d.kind ?? 'Conversion' }));

  await page.route('**/api/auth/login', route => route.fulfill({ json: { accessToken: 't', mustChangePassword: false, username: 'reviewer', role: options.role ?? 'superadmin' } }));
  await page.route('**/api/connections', route => route.fulfill({ json: [
    { id: sourceId, providerKey: 'azure-devops-services', direction: 'source', name: 'DevOps', organization: 'source', validationCode: 'Verified' },
    { id: destinationId, providerKey: 'github-enterprise-cloud', direction: 'destination', name: 'GitHub', organization: 'target-org', validationCode: 'Verified' }] }));
  await page.route('**/api/ai-connections', route => route.fulfill({ json: [{
    id: aiId, name: 'Corporate model', provider: 'OpenAICompatible', endpoint: 'https://llm.corp.example/v1', model: 'gpt-4.1', auth: 'ApiKey', verificationCode: 'Verified', sendSourceContent: true }] }));
  await page.route('**/api/workloads/pipelines/convert/status', route => route.fulfill({ json: { actionlint: true, zizmor: true, canBeApprovable: true, policyVersion: 'x', actionCatalogVerifiedOn: 'y' } }));
  await page.route('**/api/workloads/pipelines/convert', route => route.fulfill({ json: convertAnswer(store.current) }));
  await page.route('**/api/workloads/pipelines/publication/status', route => route.fulfill({ json: {
    enabled: options.publication?.enabled ?? false, allowedRepositories: options.publication?.allowed ?? [] } }));
  await page.route(/\/api\/workloads\/pipelines\/proposals/, async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (request.method() === 'GET' && path.endsWith('/proposals')) {
      return options.listFails ? route.fulfill({ status: 500, json: { error: 'x' } }) : route.fulfill({ json: summaries() });
    }
    const id = /proposals\/([0-9a-f-]{36})/.exec(path)?.[1];
    const target = [store.current, ...store.kept].find(d => d.id === id);
    if (!target) return route.fulfill({ status: 404, json: {} });
    if (request.method() === 'GET' && path.endsWith('/publications')) return route.fulfill({ json: store.attempts });
    if (request.method() === 'GET') return route.fulfill({ json: target });
    const verb = path.split('/').pop()!;
    const body = request.postDataJSON() as Record<string, any>;
    if (verb === 'publish') {
      store.publishes.push(body);
      if (options.publishRefuse) return route.fulfill({ status: options.publishRefuse.status, json: options.publishRefuse.body });
      const record = options.published ?? publicationRecord();
      store.attempts = [record, ...store.attempts];
      return route.fulfill({ json: record });
    }
    if (verb === 'derive-triggers') {
      store.derives.push(id!);
      if (options.deriveRefuse) return route.fulfill({ status: options.deriveRefuse.status, json: options.deriveRefuse.body });
      const created = options.derived ?? triggersDetail(id!);
      store.kept = [store.current, ...store.kept.filter(k => k.id !== store.current.id)];
      store.current = created;
      return route.fulfill({ json: created });
    }
    if (verb === 'publication-plan') {
      store.plans.push(body);
      if (options.planRefuse) return route.fulfill({ status: options.planRefuse.status, json: options.planRefuse.body });
      return route.fulfill({ json: options.plan ?? publicationPlan(body.repository) });
    }
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

export const open = (page: Page) => page.getByRole('button', { name: /proponi una conversione con ai|propose a conversion with ai/i }).click();
export const propose = (page: Page) => page.getByRole('button', { name: /proponi un workflow|propose a workflow/i }).click();
export const approve = (page: Page) => page.getByRole('button', { name: /approva esattamente questo testo|approve this exact text/i });
export const reject = (page: Page) => page.getByRole('button', { name: /^rifiuta$|^reject$/i });
export const revoke = (page: Page) => page.getByRole('button', { name: /revoca l.approvazione|revoke the approval/i });
export const note = (page: Page) => page.getByRole('textbox', { name: /nota \(obbligatoria|note \(required/i });
export const review = (page: Page) => page.getByLabel(/revisione umana|human review/i);


export function publicationPlan(repository = 'orion-api', overrides: Detail = {}): Detail {
  return {
    proposalId, workflowSha256: sha, organization: 'target-org', repository, baseBranch: 'main', baseSha: 'c'.repeat(40),
    headBranch: 'migrate/azdo-123-abababab', workflowPath: '.github/workflows/azdo-123-orion-api.yml',
    commitMessage: 'ci: add Azure pipeline 123', pullRequestTitle: 'Add GitHub Actions workflow for Azure pipeline #123 (Orion API)',
    pullRequestBody: '## Azure Pipelines → GitHub Actions (proposal)\n\n| Workflow SHA-256 | `' + sha + '` |\n',
    ready: true, writesPerformed: false, findings: [],
    steps: [
      { code: 'Clone', title: 'Clone the destination repository', command: `git clone 'https://github.com/target-org/${repository}.git' && cd '${repository}'` },
      { code: 'Verify', title: 'Check that it is the approved text', command: `sha256sum '.github/workflows/azdo-123-orion-api.yml'   # must print ${sha}` },
      { code: 'OpenPullRequest', title: 'Open the pull request', command: "gh pr create --base 'main' --head 'migrate/azdo-123-abababab'" }
    ],
    planSha256: 'd'.repeat(64), readAtUtc: '2026-11-13T14:00:00Z',
    workflowYaml: '# azure-tasks: converted=UseDotNet@2 manual=none\nname: demo\non:\n  workflow_dispatch:\njobs: {}\n',
    ...overrides
  };
}

export function publicationRecord(overrides: Detail = {}): Detail {
  return {
    id: '9c3d4e5f-6a7b-4c8d-9e0f-1a2b3c4d5e6f', proposalId, state: 'Published', organization: 'target-org', repository: 'orion-api', baseBranch: 'main', baseSha: 'c'.repeat(40),
    headBranch: 'migrate/azdo-123-abababab', workflowPath: '.github/workflows/azdo-123-orion-api.yml', planSha256: 'd'.repeat(64), commitSha: 'e'.repeat(40), pullNumber: 7,
    pullUrl: 'https://github.com/target-org/orion-api/pull/7', errorCode: null, failedAtState: null, writesPossible: false, requestedBy: 'reviewer',
    startedAtUtc: '2026-11-13T14:00:00Z', updatedAtUtc: '2026-11-13T14:00:05Z', ...overrides
  };
}

export const derivedId = '8b2d3e4f-5c6a-4b7c-9d8e-0f1a2b3c4d5e';
export const derivedSha = 'cd'.repeat(32);

// The same workflow with its Azure triggers converted: another id and hash, linked to its parent, and the mapping's differences as warnings to accept.
export function triggersDetail(parentId = proposalId, overrides: Detail = {}): Detail {
  return detail({
    id: derivedId, kind: 'Triggers', parentProposalId: parentId, workflowSha256: derivedSha, canApprove: true, modelNotes: null,
    workflowYaml: '# azure-tasks: converted=UseDotNet@2 manual=none\nname: demo\non:\n  workflow_dispatch:\n  push:\n    branches:\n      - \'main\'\njobs: {}\n',
    triggers: {
      mapped: ['push — branches: main, release/**', 'pull_request — branches: main', 'schedule: 0 3 * * 1-5'],
      differences: [
        { code: 'TRIGGER_PR_AZURE_REPOS_IGNORED', message: 'In Azure Repos Git the pr: section of the YAML is ignored.' },
        { code: 'TRIGGER_STARTS_PRIVILEGED_JOBS', message: 'The workflow uses secrets and will start by itself.' }
      ]
    },
    findings: [
      { gate: 'triggers', code: 'TRIGGER_PR_AZURE_REPOS_IGNORED', severity: 'Warning', message: 'In Azure Repos Git the pr: section of the YAML is ignored.', line: null },
      { gate: 'triggers', code: 'TRIGGER_STARTS_PRIVILEGED_JOBS', severity: 'Warning', message: 'The workflow uses secrets and will start by itself.', line: null }
    ],
    events: [{ id: 1, type: 'Created', actor: 'author', occurredAtUtc: '2026-11-13T15:00:00Z', workflowSha256: derivedSha, note: null, reportSha256: null, policyVersion: null, acknowledgedWarnings: [] }],
    ...overrides
  });
}

export const deriveButton = (page: Page) => page.getByRole('button', { name: /proponi i trigger di azure|propose the azure triggers/i });
