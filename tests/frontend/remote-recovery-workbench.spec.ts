import { expect, test, type Page, type Route } from '@playwright/test';

const prep = '11111111-1111-4111-8111-111111111111';
const plan = '22222222-2222-4222-8222-222222222222';
const scope = '33333333-3333-4333-8333-333333333333';
const session = '44444444-4444-4444-8444-444444444444';
const intent = '55555555-5555-4555-8555-555555555555';
const confirmation = '66666666-6666-4666-8666-666666666666';
const barrierA = '77777777-7777-4777-8777-777777777777';
const barrierB = '88888888-8888-4888-8888-888888888888';
const barrierC = '99999999-9999-4999-8999-999999999999';
const sha = (symbol: string) => symbol.repeat(64);

const operationPlan = {
  id: plan, preparationId: prep, organization: 'gh-org',
  repositoryCount: 1, stepCount: 1, changedRefCount: 1,
  sensitiveChangeCount: 0, isCurrent: false,
  readyForExecution: false, steps: []
};

type RecoveryStatus =
  | 'AwaitingIndependentReadback' | 'OperatorReviewRequired'
  | 'TipsObservedNeedsClosure' | 'IndependentClosureObserved'
  | 'CheckpointedIndependentClosure';

function row(
  barrierId: string, state: RecoveryStatus,
  overrides: Record<string, unknown> = {}
) {
  const next: Record<RecoveryStatus, string> = {
    AwaitingIndependentReadback: 'ObserveTipsReadOnly',
    OperatorReviewRequired: 'OperatorReviewRequired',
    TipsObservedNeedsClosure: 'VerifyFullObjectsReadOnly',
    IndependentClosureObserved: 'CertifyIndependentClosure',
    CheckpointedIndependentClosure: 'None'
  };
  return {
    barrierId, confirmationId: confirmation, intentId: intent, sessionId: session,
    preparationId: prep, planId: plan, scopeId: scope,
    hazardId: scope, grantId: scope,
    repositoryId: 2468, organization: 'gh-org', repository: 'sandbox',
    possibleSendRecordedAtUtc: '2026-11-08T16:00:00Z', state,
    recommendedNextAction: next[state], needsOperatorAttention: true,
    typedBaselineAvailable: true, baselineRefCount: 2, createCandidateCount: 1,
    barrierEvidenceSha256: sha('a'), typedBaselineEvidenceSha256: sha('b'),
    readbackId: state === 'AwaitingIndependentReadback' ? null : scope,
    readbackEvidenceSha256: state === 'AwaitingIndependentReadback' ? null : sha('c'),
    reconciliationId: state === 'IndependentClosureObserved' ||
      state === 'CheckpointedIndependentClosure' ? scope : null,
    reconciliationEvidenceSha256: state === 'IndependentClosureObserved' ||
      state === 'CheckpointedIndependentClosure' ? sha('d') : null,
    fullObjectClosureSha256: state === 'IndependentClosureObserved' ||
      state === 'CheckpointedIndependentClosure' ? sha('e') : null,
    closureCheckpointId: state === 'CheckpointedIndependentClosure' ? scope : null,
    closureCheckpointEvidenceSha256:
      state === 'CheckpointedIndependentClosure' ? sha('f') : null,
    canPush: false, canRetry: false, canDispatch: false,
    migrationComplete: false, ...overrides
  };
}

async function login(page: Page, role: string) {
  await page.route('**/api/auth/login', route => route.fulfill({
    json: { accessToken: 'fake-recovery-token',
      mustChangePassword: false, username: role, role }
  }));
  await page.goto('/');
  await page.getByRole('textbox', { name: /nome utente|username/i }).fill(role);
  await page.getByLabel(/^password$/i).fill('password');
  await page.getByRole('button', { name: /^accedi$|^sign in$/i }).click();
  await page.getByRole('button', { name: /recupero remoto|remote recovery/i }).click();
}

async function open(page: Page) {
  await page.getByRole('textbox', {
    name: /ID preparazione incrementale|Incremental preparation ID/i
  }).fill(prep);
  await page.getByRole('button', {
    name: /apri storico recupero|open recovery history/i
  }).click();
}

function mock(page: Page, options: {
  status?: RecoveryStatus; pageTwo?: boolean; readonlyAction?: boolean;
  unsafe?: boolean; serverError?: boolean
} = {}) {
  let state = options.status ?? 'AwaitingIndependentReadback';
  const calls: string[] = [];
  let mutationCount = 0;
  void page.route('**/api/incremental-preparations/**', async (route: Route) => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    const method = request.method();
    calls.push(method + ' ' + path);
    const root = `/api/incremental-preparations/${prep}/operation-plans`;
    if (path === root && method === 'GET')
      return route.fulfill({ json: [operationPlan] });
    if (path === root + `/${plan}/remote-recovery-workbench` && method === 'GET') {
      if (options.serverError)
        return route.fulfill({ status: 409,
          json: { error: 'PostSendReadbackHistoryCorrupt' } });
      const after = url.searchParams.get('after');
      expect(Number(url.searchParams.get('take'))).toBeLessThanOrEqual(20);
      const records = after ? [row(barrierC, 'CheckpointedIndependentClosure')] :
        options.pageTwo
          ? [row(barrierA, state, options.unsafe ? { canRetry: true } : {}),
             row(barrierB, 'OperatorReviewRequired',
               { typedBaselineAvailable: false,
                 recommendedNextAction: 'OperatorReviewRequired' })]
          : [row(barrierA, state, options.unsafe ? { canRetry: true } : {})];
      return route.fulfill({ json: {
        preparationId: prep, planId: plan, repositoryId: 2468,
        items: records,
        nextAfter: options.pageTwo && !after ? barrierB : null,
        canPush: false, canRetry: false, canDispatch: false,
        migrationComplete: false
      } });
    }
    const actionBase = `${root}/${plan}/github-app-scopes/${scope}` +
      `/remote-sandbox-review-sessions/${session}/dispatch-intents/${intent}` +
      `/confirmations/${confirmation}`;
    if (options.readonlyAction && method === 'POST' &&
        path.startsWith(actionBase) &&
        request.postData() === null) {
      mutationCount++;
      if (path === `${actionBase}/independent-recovery/${barrierA}/observe-tips`)
        state = 'TipsObservedNeedsClosure';
      else if (path === `${actionBase}/independent-recovery/${barrierA}/verify-full-objects`)
        state = 'IndependentClosureObserved';
      else if (path === `${actionBase}/post-send-recovery/${barrierA}/closure-checkpoints`)
        state = 'CheckpointedIndependentClosure';
      else return route.fulfill({ status: 404, json: { error: 'WrongAction' } });
      return route.fulfill({ json: {
        state, readOnlyAcquisition: true, canPush: false,
        canRetry: false, migrationComplete: false
      } });
    }
    return route.fulfill({ status: 404, json: { error: 'UnexpectedRecoveryRoute' } });
  });
  return {
    calls: () => calls,
    actions: () => mutationCount,
    current: () => state
  };
}

test('F-088 persisted evidence timeline is paginated and never claims GitHub write success', async ({ page }) => {
  const seen = mock(page, { pageTwo: true });
  await login(page, 'superadmin');
  await expect(page.getByText('READ-ONLY / NO RETRY')).toBeVisible();
  await open(page);
  await expect(page.getByText('gh-org/sandbox')).toHaveCount(2);
  await expect(page.getByText(/Differenze remote|Remote mismatch/i)).toBeVisible();
  await expect(page.getByText(/Ref storici tipizzati|Historical typed ref/i)).toBeVisible();
  await expect(page.getByText('F-082 · Possible send').first()).toBeVisible();
  await expect(page.getByText('F-058 · Full objects').first()).toBeVisible();
  await page.getByRole('button', { name: /carica epoch precedenti|load earlier epochs/i }).click();
  await expect(page.getByText('gh-org/sandbox')).toHaveCount(3);
  await expect(page.getByText(/Checkpoint indipendente|Independent closure checkpointed/i)).toBeVisible();
  expect(seen.actions()).toBe(0);
  expect(seen.calls().every(x => x.startsWith('GET '))).toBe(true);
});

test('F-088 manual independent actions are explicitly confirmed and body-free, never push', async ({ page }) => {
  const seen = mock(page, { readonlyAction: true });
  page.on('dialog', dialog => dialog.accept());
  await login(page, 'superadmin');
  await open(page);
  await page.getByRole('button', {
    name: /verifica i ref|observe tips/i
  }).click();
  await expect(page.getByRole('button', {
    name: /verifica tutti gli oggetti|verify full git objects/i
  })).toBeVisible();
  await page.getByRole('button', {
    name: /verifica tutti gli oggetti|verify full git objects/i
  }).click();
  await expect(page.getByRole('button', {
    name: /certifica le prove|certify existing evidence/i
  })).toBeVisible();
  await page.getByRole('button', {
    name: /certifica le prove|certify existing evidence/i
  }).click();
  await expect(page.getByText(/Checkpoint indipendente|Independent closure checkpointed/i)).toBeVisible();
  expect(seen.current()).toBe('CheckpointedIndependentClosure');
  expect(seen.actions()).toBe(3);
  expect(seen.calls().filter(x => x.startsWith('POST '))).toHaveLength(3);
  expect(seen.calls().some(x => /push|dispatch|retry/i.test(x))).toBe(false);
});

test('F-088 a divergent remote forbids both object verification and retry', async ({ page }) => {
  const seen = mock(page, { status: 'OperatorReviewRequired' });
  await login(page, 'superadmin');
  await open(page);
  await expect(page.getByText(/revisione manuale|manual review required/i)).toBeVisible();
  await expect(page.getByRole('button', { name: /verifica tutti gli oggetti|verify full/i }))
    .toHaveCount(0);
  expect(seen.actions()).toBe(0);
});

test('F-088 non-superadmins do not call recovery endpoints', async ({ page }) => {
  const seen = mock(page);
  await login(page, 'admin');
  await expect(page.getByText(/Solo i superadmin|Only superadmins/i)).toBeVisible();
  expect(seen.calls()).toHaveLength(0);
});

test('F-088 integrity errors and forged retry flags fail visibly closed', async ({ page }) => {
  mock(page, { serverError: true });
  await login(page, 'superadmin');
  await open(page);
  await expect(page.getByRole('alert').getByText(
    /PostSendReadbackHistoryCorrupt/)).toBeVisible();
  await expect(page.getByText('gh-org/sandbox')).toHaveCount(0);
});

test('F-088 server response granting retry is never rendered as safe evidence', async ({ page }) => {
  mock(page, { unsafe: true });
  await login(page, 'superadmin');
  await open(page);
  await expect(page.getByRole('alert').getByText(
    /RecoveryWorkbenchUnsafeResponse/)).toBeVisible();
  await expect(page.getByText('gh-org/sandbox')).toHaveCount(0);
});
