import { expect, test, type Page, type Route } from '@playwright/test';

const preparationId = '11111111-1111-4111-8111-111111111111';
const planId = '22222222-2222-4222-8222-222222222222';
const sourceConnectionId = '33333333-3333-4333-8333-333333333333';
const snapshotId = '44444444-4444-4444-8444-444444444444';
const jobId = '55555555-5555-4555-8555-555555555555';
const barrierId = '66666666-6666-4666-8666-666666666666';
const stamp = '2026-11-08T15:30:00Z';

const plan = {
  id: planId, preparationId, snapshotId,
  sourceConnectionId, organization: 'gh-org', repositoryCount: 2,
  isCurrent: true, manifestSha256: 'a'.repeat(64),
  readyForExecution: false, steps: []
};
const first = {
  sourceRepositoryId: 'repo-1', targetRepositoryName: 'migration-one',
  addedRefs: 2, changedRefs: 1, restoredRefs: 0, missingRefs: 0,
  plannedRefChanges: 3, totalRefChanges: 3,
  stepState: 'Blocked', stepBlocker: 'DestinationWritePermissionUnverified',
  recoveryTracking: 'NoMarkerRecorded', recoveryState: null,
  latestBarrierId: null, latestPossibleSendUtc: null,
  canOpenRecovery: false, canPush: false, canRetry: false,
  migrationComplete: false
};
const second = {
  ...first, sourceRepositoryId: 'repo-2',
  targetRepositoryName: 'migration-two', addedRefs: 0,
  changedRefs: 0, plannedRefChanges: 0, totalRefChanges: 0,
  recoveryTracking: 'NotMonitored'
};
const pulse = {
  preparationId, planId, sourceConnectionId, snapshotId,
  organization: 'gh-org', manifestSha256: 'a'.repeat(64),
  planIsCurrent: true, readyForExecution: false,
  preparationState: 'Prepared',
  stagingState: 'LatestRecordedSnapshot',
  latestKnownSnapshotId: snapshotId, sourceCollectedAtUtc: stamp,
  stagingAppliedAtUtc: stamp, latestKnownStagingAppliedAtUtc: stamp,
  sourceInventoryComplete: false, stagingObserved: 9,
  stagingAdded: 5, stagingChanged: 1, stagingUnchanged: 3,
  stagingMarkedMissing: 0, journalAdded: 5, journalChanged: 1,
  journalRestored: 0, journalMissing: 0, latestStageJobId: null,
  stageJobState: 'DirectStagingWithoutJob',
  stageJobErrorCode: null, stageJobUpdatedAtUtc: null,
  repositories: [first, second],
  providerSourceFreshnessVerified: false, canPush: false,
  canRetry: false, migrationComplete: false
};

async function login(page: Page, role = 'superadmin') {
  await page.route('**/api/auth/login', route => route.fulfill({
    json: { accessToken: 'test-source-token',
      mustChangePassword: false, role, username: role }
  }));
  await page.goto('/');
  await page.getByRole('textbox', { name: /nome utente|username/i }).fill(role);
  await page.getByLabel(/^password$/i).fill('password');
  await page.getByRole('button', { name: /^accedi$|^sign in$/i }).click();
  await page.getByRole('button', { name: /sincronizzazione sorgenti|source sync/i }).click();
}

async function open(page: Page) {
  await page.getByRole('textbox', {
    name: /UUID preparazione incrementale|Incremental preparation UUID/i
  }).fill(preparationId);
  await page.getByRole('button', {
    name: /Apri sincronizzazione|Open source sync/i
  }).click();
}

function routes(page: Page, options: {
  stale?: boolean; unsafe?: boolean; failed?: boolean;
  recovered?: boolean
} = {}) {
  const seen: string[] = [];
  let enqueues = 0;
  let jobReads = 0;
  const job = {
    id: jobId, kind: 'DiscoverSource', sourceConnectionId,
    snapshotId: jobId, state: 'Queued', attempts: 0, maxAttempts: 5,
    availableAtUtc: stamp, createdAtUtc: stamp, updatedAtUtc: stamp,
    completedAtUtc: null, lastErrorCode: null, result: null
  };
  void page.route('**/api/incremental-preparations/**', async (route: Route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const method = request.method();
    seen.push(method + ' ' + path);
    const base = `/api/incremental-preparations/${preparationId}/operation-plans`;
    if (method === 'GET' && path === base)
      return route.fulfill({ json: [plan] });
    if (method === 'GET' &&
        path === base + `/${planId}/repository-sync-pulse`) {
      if (options.failed)
        return route.fulfill({ status: 409,
          json: { error: 'MultiRepositoryPulseJournalCorrupt' } });
      return route.fulfill({ json: {
        ...pulse,
        stagingState: options.stale
          ? 'SupersededInPostgres' : 'LatestRecordedSnapshot',
        latestKnownSnapshotId: options.stale ? jobId : snapshotId,
        latestKnownStagingAppliedAtUtc: options.stale
          ? '2026-11-08T18:00:00Z' : stamp,
        canRetry: options.unsafe ?? false,
        repositories: options.recovered ? [
          { ...first, recoveryTracking: 'PossibleSendEvidence',
            recoveryState: 'TipsObservedNeedsClosure',
            latestBarrierId: barrierId, latestPossibleSendUtc: stamp,
            canOpenRecovery: true }, second] : [first, second]
      } });
    }
    return route.fulfill({ status: 404,
      json: { error: 'UnexpectedSyncRoute' } });
  });
  void page.route('**/api/jobs/**', async (route: Route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    seen.push(request.method() + ' ' + path);
    if (request.method() === 'POST' && path === '/api/jobs/discovery') {
      enqueues++;
      const requestBody = request.postDataJSON() as {
        connectionId: string; requestId: string;
      };
      expect(requestBody.connectionId).toBe(sourceConnectionId);
      expect(requestBody.requestId).toMatch(
        /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i);
      return route.fulfill({ status: 202, json: job });
    }
    if (request.method() === 'GET' && path === `/api/jobs/${jobId}`) {
      jobReads++;
      return route.fulfill({ json: {
        ...job, state: 'Succeeded', attempts: 1,
        completedAtUtc: stamp
      } });
    }
    return route.fulfill({ status: 404,
      json: { error: 'UnexpectedJobRoute' } });
  });
  return { requests: () => seen, enqueues: () => enqueues,
    jobReads: () => jobReads };
}

test('F-089 two-repo PostgreSQL pulse retains per-repository source counts without GitHub claims', async ({ page }) => {
  const api = routes(page);
  await login(page);
  await expect(page.getByText(/NESSUN PUSH GITHUB|NO GITHUB PUSH/)).toBeVisible();
  await open(page);
  await expect(page.getByText('migration-one')).toBeVisible();
  await expect(page.getByText('migration-two')).toBeVisible();
  await expect(page.getByText(/GitHub non monitorato|Not monitored on GitHub/)).toBeVisible();
  await expect(page.getByText(/Nessun marker di invio|No possible-send marker/)).toBeVisible();
  await expect(page.getByText(/copertura dell'inventario|inventory coverage is partial/i)).toBeVisible();
  await expect(page.getByRole('button', { name: /Apri recupero|Open recovery/i }))
    .toHaveCount(0);
  await page.getByRole('textbox', { name: /Filtra repository|Filter by repository/i })
    .fill('repo-2');
  await expect(page.getByText('migration-one')).toHaveCount(0);
  await expect(page.getByText('migration-two')).toBeVisible();
  await page.getByRole('textbox', { name: /Filtra repository|Filter by repository/i })
    .fill('');
  await page.getByRole('checkbox', { name: /Solo con modifiche|Changed only/i })
    .check();
  await expect(page.getByText('migration-one')).toBeVisible();
  await expect(page.getByText('migration-two')).toHaveCount(0);
  expect(api.requests().every(x => x.startsWith('GET '))).toBe(true);
});

test('F-089 a newer source staging snapshot marks the reviewed plan historical', async ({ page }) => {
  const api = routes(page, { stale: true });
  await login(page);
  await open(page);
  await expect(page.getByText(/Esiste uno staging sorgente più recente|Newer source staging exists/i))
    .toBeVisible();
  await expect(page.getByText(/Questo piano usa uno snapshot precedente|uses an older source snapshot/i))
    .toBeVisible();
  expect(api.enqueues()).toBe(0);
});

test('F-089 superadmin explicitly queues Azure read plus PostgreSQL staging and checks durable job', async ({ page }) => {
  const api = routes(page);
  page.on('dialog', dialog => dialog.accept());
  await login(page);
  await open(page);
  await page.getByRole('button', {
    name: /Accoda aggiornamento sorgente|Queue source refresh/i
  }).click();
  await expect(page.getByText(/Job aggiornamento sorgente accodato|Source refresh job queued/i))
    .toBeVisible();
  await page.getByRole('button', { name: /Controlla job|Check job/i }).click();
  await expect(page.getByText(/Discovery e staging completati|Source discovery\/staging completed/i))
    .toBeVisible();
  expect(api.enqueues()).toBe(1);
  expect(api.jobReads()).toBe(1);
  expect(api.requests().filter(x => x.startsWith('POST ')))
    .toEqual(['POST /api/jobs/discovery']);
  expect(api.requests().some(x => /github.*push|git-push|retry-push/.test(x)))
    .toBe(false);
});

test('F-089 only the exact sandbox-mapped repository links to read-only F-088 recovery', async ({ page }) => {
  routes(page, { recovered: true });
  await login(page);
  await open(page);
  await expect(page.getByRole('button', {
    name: /Apri recupero|Open recovery/i })).toHaveCount(1);
  await page.getByRole('button', { name: /Apri recupero|Open recovery/i }).click();
  await expect(page.getByRole('heading', {
    name: /Recupero remoto|Remote recovery/i
  })).toBeVisible();
});

test('F-089 other roles never call the source sync pulse endpoint', async ({ page }) => {
  const api = routes(page);
  await login(page, 'admin');
  await expect(page.getByText(/Solo i superadmin|Only superadmins/i)).toBeVisible();
  expect(api.requests()).toHaveLength(0);
});

test('F-089 rejects forged retry flags and surfaces journal corruption', async ({ page }) => {
  routes(page, { unsafe: true });
  await login(page);
  await open(page);
  await expect(page.getByRole('alert').getByText(
    /MultiRepositoryPulseUnsafeResponse/)).toBeVisible();
  await expect(page.getByText('migration-one')).toHaveCount(0);
});

test('F-089 API integrity conflicts are visible rather than empty success', async ({ page }) => {
  routes(page, { failed: true });
  await login(page);
  await open(page);
  await expect(page.getByRole('alert').getByText(
    /MultiRepositoryPulseJournalCorrupt/)).toBeVisible();
  await expect(page.getByText('migration-one')).toHaveCount(0);
});
