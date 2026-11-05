import { expect, test, type Page, type Route } from '@playwright/test';

const preparationId = '11111111-1111-4111-8111-111111111111';
const planId = '22222222-2222-4222-8222-222222222222';
const snapshotId = '33333333-3333-4333-8333-333333333333';
const observationId = '44444444-4444-4444-8444-444444444444';
const comparisonId = '55555555-5555-4555-8555-555555555555';
const planSha = 'a'.repeat(64);
const targetSha = 'b'.repeat(64);
const sourceSha = 'c'.repeat(64);
const comparisonSha = 'd'.repeat(64);
const oidA = 'a'.repeat(40);
const oidB = 'b'.repeat(40);
const timestamp = '2026-11-06T09:30:00Z';

const preparation = {
  id: preparationId, snapshotId, sourceConnectionId: snapshotId,
  destinationConnectionId: observationId, state: 'Prepared', itemCount: 4
};
const plan = {
  id: planId, preparationId, snapshotId, manifestSha256: planSha,
  createdAtUtc: timestamp, isCurrent: true, readyForExecution: false,
  steps: [{ ordinal: 1, kind: 'ReviewTargetName', state: 'Blocked',
    sourceRepositoryId: 'repo-1', targetRepositoryName: 'github-api',
    blockerCode: 'TargetAvailabilityUnverified' }]
};
const observation = {
  id: observationId, planId, sourceRepositoryId: 'repo-1',
  targetRepositoryName: 'github-api', organization: 'gh-org',
  manifestSha256: planSha, evidenceSha256: targetSha,
  observedAtUtc: timestamp, refCount: 4, isCurrent: true,
  readyForExecution: false
};
const comparison = {
  id: comparisonId, planId, sourceRepositoryId: 'repo-1',
  targetRepositoryName: 'github-api', snapshotId, targetObservationId: observationId,
  manifestSha256: planSha, sourceEvidenceSha256: sourceSha,
  targetEvidenceSha256: targetSha, reconciliationSha256: comparisonSha,
  sourceManifestRootSha256: null, createdAtUtc: timestamp, sourceRefCount: 3,
  targetRefCount: 3, matchingCount: 1, createCandidateCount: 1,
  divergedCount: 1, targetOnlyCount: 1, isCurrent: true,
  readyForExecution: false
};
const rows = [
  { name: 'refs/heads/main', sourceObjectId: oidA, targetObjectId: oidA,
    disposition: 0, blockerCode: 'None' },
  { name: 'refs/heads/missing', sourceObjectId: oidA, targetObjectId: null,
    disposition: 1, blockerCode: 'DestinationWritePermissionUnverified' },
  { name: 'refs/heads/release', sourceObjectId: oidA, targetObjectId: oidB,
    disposition: 2, blockerCode: 'RefFastForwardUnverified' },
  { name: 'refs/tags/old', sourceObjectId: null, targetObjectId: oidB,
    disposition: 3, blockerCode: 'TargetRefDeletionProhibited' }
];

async function login(page: Page, role: string) {
  await page.route('**/api/auth/login', route => route.fulfill({ json: {
    accessToken: 'test-token', mustChangePassword: false, username: role, role
  } }));
  await page.goto('/');
  await page.getByRole('textbox', { name: /nome utente|username/i }).fill(role);
  await page.getByLabel(/^password$/i).fill('test-password');
  await page.getByRole('button', { name: /^accedi$|^sign in$/i }).click();
}

type MockOptions = {
  stalePlan?: boolean; staleTarget?: boolean; paged?: boolean;
  failDetail?: boolean; startWithEvidence?: boolean; startWithHistory?: boolean;
};

async function mockReconciliation(page: Page, options: MockOptions = {}) {
  let collected = !!options.startWithEvidence;
  let compared = !!options.startWithHistory;
  let reads = 0;
  let captures = 0;
  let comparisons = 0;
  let paging = 0;
  const unexpected: string[] = [];

  await page.route('**/api/incremental-preparations/**', async (route: Route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const method = request.method();
    const base = `/api/incremental-preparations/${preparationId}`;
    const planBase = `${base}/operation-plans/${planId}`;

    if (path === base && method === 'GET') return route.fulfill({ json: preparation });
    if (path === `${base}/operation-plans` && method === 'GET')
      return route.fulfill({ json: [{ ...plan, isCurrent: !options.stalePlan }] });
    if (path === planBase && method === 'GET')
      return route.fulfill({ json: { ...plan, isCurrent: !options.stalePlan } });

    if (path === `${planBase}/target-ref-evidence`) {
      if (method === 'GET') {
        reads++;
        return route.fulfill({ json: collected ?
          [{ ...observation, isCurrent: !options.staleTarget }] : [] });
      }
      if (method === 'POST') {
        captures++;
        expect(request.postDataJSON()).toEqual({
          sourceRepositoryId: 'repo-1', expectedManifestSha256: planSha
        });
        collected = true;
        return route.fulfill({ json: observation });
      }
    }
    if (path === `${planBase}/ref-reconciliations`) {
      if (method === 'GET') {
        reads++;
        return route.fulfill({ json: compared ? [
          { ...comparison, isCurrent: !options.staleTarget && !options.stalePlan }
        ] : [] });
      }
      if (method === 'POST') {
        comparisons++;
        expect(request.postDataJSON()).toEqual({
          sourceRepositoryId: 'repo-1', targetObservationId: observationId,
          expectedManifestSha256: planSha, expectedTargetEvidenceSha256: targetSha
        });
        compared = true;
        return route.fulfill({ json: comparison });
      }
    }
    if (path === `${planBase}/ref-reconciliations/${comparisonId}` &&
      method === 'GET') {
      paging++;
      if (options.failDetail) return route.fulfill({
        status: 409, json: { error: 'ReconciliationIntegrityFailed' }
      });
      const after = new URL(request.url()).searchParams.get('after');
      const part = options.paged ?
        after ? rows.slice(1) : rows.slice(0, 1) : rows;
      return route.fulfill({ json: {
        reconciliation: comparison, items: part,
        nextAfter: options.paged && !after ? 'refs/heads/main' : null
      } });
    }
    unexpected.push(method + ' ' + path);
    return route.fulfill({ status: 404, json: { error: 'UnexpectedTestApiRoute' } });
  });

  return {
    counters: () => ({ reads, captures, comparisons, paging, unexpected })
  };
}

test('F-033 operator collects target refs, compares snapshots and reviews four blocked decisions', async ({ page }) => {
  const mocked = await mockReconciliation(page);
  await login(page, 'admin');
  await page.getByRole('button', { name: /Riconciliazione ref Git|Git ref reconciliation/i }).click();

  await expect(page.getByText(/Solo revisione|Review only/i)).toBeVisible();
  const submit = page.getByRole('button', { name: /Apri preparazione|Open preparation/i });
  await expect(submit).toBeDisabled();
  await page.getByRole('textbox', { name: /ID preparazione incrementale|Incremental preparation ID/i })
    .fill(preparationId);
  await expect(submit).toBeEnabled();
  await submit.click();

  await expect(page.getByRole('combobox', { name: /Repository sorgente|Source → destination repository/i })).toHaveValue('repo-1');
  await expect(page.getByText(/Bloccata — nessuna scrittura|Blocked — no writes/)).toBeVisible();
  await expect(page.getByRole('combobox', { name: /Osservazione target salvata|Saved target observation/i })).toHaveValue('');
  await expect(page.getByRole('button', { name: /Confronta ref certificati|Compare verified refs/i }))
    .toBeDisabled();

  await page.getByRole('button', { name: /Acquisisci ref GitHub live|Collect live GitHub refs/i }).click();
  await expect(page.getByText(/Nuova osservazione GitHub|A new SHA-256-bound target observation/))
    .toBeVisible();
  await expect(page.getByRole('button', { name: /Confronta ref certificati|Compare verified refs/i }))
    .toBeEnabled();
  await page.getByRole('button', { name: /Confronta ref certificati|Compare verified refs/i }).click();
  await expect(page.getByText(/Confronto sorgente-target|deterministic source-to-target comparison/))
    .toBeVisible();

  await expect(page.getByText('refs/heads/main')).toBeVisible();
  await expect(page.getByText('refs/heads/missing')).toBeVisible();
  await expect(page.getByText('refs/heads/release')).toBeVisible();
  await expect(page.getByText('refs/tags/old')).toBeVisible();
  await expect(page.getByText('DestinationWritePermissionUnverified')).toBeVisible();
  await expect(page.getByText('RefFastForwardUnverified')).toBeVisible();
  await expect(page.getByText('TargetRefDeletionProhibited')).toBeVisible();
  await expect(page.getByText(/Candidato creazione|Creation candidate/i)).toBeVisible();
  await expect(page.getByText(/Target preservato|Target preserved/i)).toBeVisible();
  expect(mocked.counters()).toMatchObject({
    captures: 1, comparisons: 1, paging: 1, unexpected: []
  });

  await page.getByRole('button', { name: /Visualizza decisioni ref|View ref decisions/i }).click();
  expect(mocked.counters()).toMatchObject({
    captures: 1, comparisons: 1, paging: 2, unexpected: []
  });
});

test('F-033 server keyset paging carries encoded cursor and loads more without replaying collection', async ({ page }) => {
  const mocked = await mockReconciliation(page, {
    startWithEvidence: true, startWithHistory: true, paged: true
  });
  await login(page, 'admin');
  await page.getByRole('button', { name: /Riconciliazione ref Git|Git ref reconciliation/i }).click();
  await page.getByRole('textbox', { name: /ID preparazione incrementale|Incremental preparation ID/i })
    .fill(preparationId);
  await page.getByRole('button', { name: /Apri preparazione|Open preparation/i }).click();
  await page.getByRole('button', { name: /Visualizza decisioni ref|View ref decisions/i }).click();

  await expect(page.getByText('refs/heads/main')).toBeVisible();
  await expect(page.getByText('refs/heads/release')).toHaveCount(0);
  await page.getByRole('button', { name: /Carica altri 50 ref|Load next 50 refs/i }).click();
  await expect(page.getByText('refs/heads/release')).toBeVisible();
  await expect(page.getByText('refs/tags/old')).toBeVisible();
  expect(mocked.counters()).toMatchObject({
    captures: 0, comparisons: 0, paging: 2, unexpected: []
  });
});

test('F-033 stale plans and target observations are reviewable but cannot trigger provider capture or compare', async ({ page }) => {
  const mocked = await mockReconciliation(page, {
    stalePlan: true, staleTarget: true, startWithEvidence: true, startWithHistory: true
  });
  await login(page, 'admin');
  await page.getByRole('button', { name: /Riconciliazione ref Git|Git ref reconciliation/i }).click();
  await page.getByRole('textbox', { name: /ID preparazione incrementale|Incremental preparation ID/i })
    .fill(preparationId);
  await page.getByRole('button', { name: /Apri preparazione|Open preparation/i }).click();

  await expect(page.getByText(/Il piano non è più corrente|The plan is no longer current/i))
    .toBeVisible();
  await expect(page.getByRole('button', { name: /Acquisisci ref GitHub live|Collect live GitHub refs/i }))
    .toBeDisabled();
  await expect(page.getByRole('button', { name: /Confronta ref certificati|Compare verified refs/i }))
    .toBeDisabled();
  await page.getByRole('button', { name: /Visualizza decisioni ref|View ref decisions/i }).click();
  await expect(page.getByText('refs/heads/main')).toBeVisible();
  expect(mocked.counters()).toMatchObject({
    captures: 0, comparisons: 0, paging: 1, unexpected: []
  });
});

test('F-033 integrity failures remain errors, never silent empty comparisons', async ({ page }) => {
  await mockReconciliation(page, { startWithHistory: true, startWithEvidence: true, failDetail: true });
  await login(page, 'admin');
  await page.getByRole('button', { name: /Riconciliazione ref Git|Git ref reconciliation/i }).click();
  await page.getByRole('textbox', { name: /ID preparazione incrementale|Incremental preparation ID/i })
    .fill(preparationId);
  await page.getByRole('button', { name: /Apri preparazione|Open preparation/i }).click();
  await page.getByRole('button', { name: /Visualizza decisioni ref|View ref decisions/i }).click();
  await expect(page.getByRole('alert').getByText('ReconciliationIntegrityFailed')).toBeVisible();
});

test('F-033 regular users cannot fetch ref comparisons or target observations', async ({ page }) => {
  let apiCalls = 0;
  await page.route('**/api/incremental-preparations/**', route => {
    apiCalls++;
    return route.fulfill({ status: 403 });
  });
  await login(page, 'user');
  await page.getByRole('button', { name: /Riconciliazione ref Git|Git ref reconciliation/i }).click();
  await expect(page.getByText(/Solo gli amministratori|Only platform administrators/i)).toBeVisible();
  expect(apiCalls).toBe(0);
});
