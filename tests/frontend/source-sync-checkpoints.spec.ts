import { expect, test, type Page, type Route } from '@playwright/test';

const prep = '11111111-1111-4111-8111-111111111111';
const plan = '22222222-2222-4222-8222-222222222222';
const source = '33333333-3333-4333-8333-333333333333';
const original = '44444444-4444-4444-8444-444444444444';
const next = '55555555-5555-4555-8555-555555555555';
const old = '66666666-6666-4666-8666-666666666666';
const last = '77777777-7777-4777-8777-777777777777';
const sha = (c: string) => c.repeat(64);
const stamp = '2026-11-08T15:30:00Z';
const operation = {
  id: plan, preparationId: prep, snapshotId: original,
  sourceConnectionId: source, organization: 'gh-org',
  manifestSha256: sha('a'), repositoryCount: 2,
  readyForExecution: false, isCurrent: true, steps: []
};
const repo1 = {
  sourceRepositoryId: 'repo-1', targetRepositoryName: 'migration-one',
  addedRefs: 2, changedRefs: 1, restoredRefs: 0, missingRefs: 0,
  plannedRefChanges: 3, totalRefChanges: 3,
  stepState: 'Blocked', stepBlocker: 'DestinationWritePermissionUnverified',
  recoveryTracking: 'NotMonitored', recoveryState: null,
  latestBarrierId: null, latestPossibleSendUtc: null,
  canOpenRecovery: false, canPush: false, canRetry: false,
  migrationComplete: false
};
const repo2 = {
  ...repo1, sourceRepositoryId: 'repo-2',
  targetRepositoryName: 'migration-two', addedRefs: 0,
  changedRefs: 0, plannedRefChanges: 0, totalRefChanges: 0
};
const pulse = {
  preparationId: prep, planId: plan, sourceConnectionId: source,
  snapshotId: original, organization: 'gh-org',
  manifestSha256: sha('a'), planIsCurrent: true,
  readyForExecution: false, preparationState: 'Prepared',
  stagingState: 'LatestRecordedSnapshot', latestKnownSnapshotId: original,
  sourceCollectedAtUtc: stamp, stagingAppliedAtUtc: stamp,
  latestKnownStagingAppliedAtUtc: stamp, sourceInventoryComplete: false,
  stagingObserved: 6, stagingAdded: 3, stagingChanged: 1,
  stagingUnchanged: 2, stagingMarkedMissing: 0, journalAdded: 3,
  journalChanged: 1, journalRestored: 0, journalMissing: 0,
  latestStageJobId: null, stageJobState: 'DirectStagingWithoutJob',
  stageJobErrorCode: null, stageJobUpdatedAtUtc: null,
  repositories: [repo1, repo2], providerSourceFreshnessVerified: false,
  canPush: false, canRetry: false, migrationComplete: false
};

function epoch(index: number) {
  const baseline = index === 0;
  const id = baseline ? old : last;
  const snapshotId = baseline ? original : next;
  const previousId = baseline ? null : old;
  const previousSha = baseline ? null : sha('b');
  return {
    id, proof: {
      preparationId: prep, planId: plan, sourceConnectionId: source,
      snapshotId, baselineSnapshotId: original,
      previousCheckpointId: previousId,
      previousEvidenceSha256: previousSha, manifestSha256: sha('a'),
      sourceCollectedAtUtc: stamp, stagingAppliedAtUtc: stamp,
      sourceInventoryComplete: false, isBaselineSnapshot: baseline,
      journalAdded: baseline ? 3 : 1, journalChanged: baseline ? 1 : 0,
      journalRestored: 0, journalMissing: 0,
      totalRefChanges: baseline ? 3 : 1,
      unmappedRefChanges: 0,
      repositories: [
        { sourceRepositoryId: 'repo-1', targetRepositoryName: 'migration-one',
          added: baseline ? 2 : 0, changed: baseline ? 1 : 0,
          restored: 0, missing: 0, total: baseline ? 3 : 0 },
        { sourceRepositoryId: 'repo-2', targetRepositoryName: 'migration-two',
          added: baseline ? 0 : 1, changed: 0,
          restored: 0, missing: 0, total: baseline ? 0 : 1 }
      ]
    },
    evidenceSha256: baseline ? sha('b') : sha('c'),
    recordedBy: source, recordedAtUtc: stamp,
    canPush: false, canRetry: false, canDispatch: false,
    migrationComplete: false
  };
}

function mock(page: Page, mode: 'normal' | 'unsafe' | 'conflict' = 'normal') {
  const requests: Array<{ path: string; method: string; body: any }> = [];
  let records = 0;
  void page.route('**/api/incremental-preparations/**', async (route: Route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const method = request.method();
    requests.push({ path, method, body: request.postDataJSON?.() });
    const base = `/api/incremental-preparations/${prep}/operation-plans`;
    if (method === 'GET' && path === base)
      return route.fulfill({ json: [operation] });
    if (method === 'GET' && path === base + `/${plan}/repository-sync-pulse`)
      return route.fulfill({ json: pulse });
    const check = base + `/${plan}/source-sync-checkpoints`;
    if (method === 'GET' && path === check)
      return route.fulfill({ json: {
        preparationId: prep, planId: plan,
        items: Array.from({ length: records }, (_, i) =>
          epoch(records - i - 1)),
        nextAfter: null,
        latestCheckpointId: records === 0 ? null : records === 1 ? old : last,
        nextEligibleSnapshotId: records === 0 ? original :
          records === 1 ? next : null,
        latestEvidenceSha256: records === 0 ? null :
          records === 1 ? sha('b') : sha('c'),
        canPush: mode === 'unsafe', canRetry: false,
        canDispatch: false, migrationComplete: false
      } });
    if (method === 'POST' && path.startsWith(check + '/')) {
      if (mode === 'conflict')
        return route.fulfill({ status: 409,
          json: { error: 'SourceSyncCheckpointCursorChanged' } });
      const body = request.postDataJSON() as {
        expectedManifestSha256: string;
        expectedPreviousCheckpointId: string | null;
      };
      expect(body.expectedManifestSha256).toBe(sha('a'));
      expect(body.expectedPreviousCheckpointId).toBe(
        records === 0 ? null : old);
      expect(path).toBe(check + '/' + (records === 0 ? original : next));
      records++;
      return route.fulfill({ json: epoch(records - 1) });
    }
    return route.fulfill({ status: 404,
      json: { error: 'UnexpectedSourceCheckpointRoute' } });
  });
  return { requests: () => requests, saved: () => records };
}

async function login(page: Page, role = 'superadmin') {
  await page.route('**/api/auth/login', route => route.fulfill({
    json: { accessToken: 'fake', mustChangePassword: false,
      username: role, role }
  }));
  await page.goto('/');
  await page.getByRole('textbox', { name: /nome utente|username/i })
    .fill(role);
  await page.getByLabel(/^password$/i).fill('password');
  await page.getByRole('button', { name: /^accedi$|^sign in$/i })
    .click();
  await page.getByRole('button', {
    name: /Sincronizzazione sorgenti|Source sync/i
  }).click();
}
async function open(page: Page) {
  await page.getByRole('textbox', {
    name: /UUID preparazione incrementale|Incremental preparation UUID/i
  }).fill(prep);
  await page.getByRole('button', {
    name: /Apri sincronizzazione|Open source sync/i
  }).click();
}

test('F-090 manual baseline and next snapshot are pinned, sequenced and never trigger GitHub push', async ({ page }) => {
  const service = mock(page);
  page.on('dialog', dialog => dialog.accept());
  await login(page);
  await open(page);
  await expect(page.getByText(/Checkpoint sorgente persistenti|Durable source checkpoints/i))
    .toBeVisible();
  await page.getByRole('button', {
    name: /Registra prossimo checkpoint|Record next checkpoint/i
  }).click();
  await expect(page.getByText(/Snapshot originale del piano|Original plan baseline/i))
    .toBeVisible();
  await page.getByRole('button', {
    name: /Registra prossimo checkpoint|Record next checkpoint/i
  }).click();
  await expect(page.getByText(/Snapshot incrementale sorgente|Incremental source snapshot/i))
    .toBeVisible();
  await expect(page.getByRole('button', {
    name: /Registra prossimo checkpoint|Record next checkpoint/i
  })).toHaveCount(0);
  expect(service.saved()).toBe(2);
  expect(service.requests().filter(x => x.method === 'POST')).toHaveLength(2);
  expect(service.requests().some(x =>
    /git-push|send|retry-push|dispatch-confirm/i.test(x.path))).toBe(false);
});

test('F-090 refuses a server page claiming GitHub write authority', async ({ page }) => {
  const service = mock(page, 'unsafe');
  await login(page);
  await open(page);
  await expect(page.getByRole('alert').getByText(
    /SourceSyncCheckpointUnsafeResponse/)).toBeVisible();
  await expect(page.getByRole('button', {
    name: /Registra prossimo checkpoint|Record next checkpoint/i
  })).toHaveCount(0);
  expect(service.saved()).toBe(0);
});

test('F-090 stale predecessor compare-and-set conflict is visible without replay', async ({ page }) => {
  const service = mock(page, 'conflict');
  page.on('dialog', dialog => dialog.accept());
  await login(page);
  await open(page);
  await page.getByRole('button', {
    name: /Registra prossimo checkpoint|Record next checkpoint/i
  }).click();
  await expect(page.getByRole('alert').getByText(
    /SourceSyncCheckpointCursorChanged/)).toBeVisible();
  expect(service.saved()).toBe(0);
});

test('F-090 ordinary admin cannot read or create checkpoint evidence', async ({ page }) => {
  const service = mock(page);
  await login(page, 'admin');
  await expect(page.getByText(/Solo i superadmin|Only superadmins/i))
    .toBeVisible();
  expect(service.requests()).toHaveLength(0);
});
