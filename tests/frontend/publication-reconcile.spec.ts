import { expect, test, type Page } from '@playwright/test';
import { destinationId, detail, open, propose, publicationPlan, publicationRecord, reconciliation, setup, sha } from './proposal-fixtures';

const approved = () => detail({
  state: 'Approved', approvedSha256: sha, canApprove: false, findings: [],
  events: [
    { id: 1, type: 'Created', actor: 'author', occurredAtUtc: '2026-11-13T12:00:00Z', workflowSha256: sha, note: null, reportSha256: null, policyVersion: null, acknowledgedWarnings: [] },
    { id: 2, type: 'Approved', actor: 'second-person', occurredAtUtc: '2026-11-13T12:30:00Z', workflowSha256: sha, note: null, reportSha256: 'c'.repeat(64), policyVersion: '2026-11-1', acknowledgedWarnings: [] }
  ]
});

const panel = (page: Page) => page.getByLabel(/prepara la pull request|prepare the pull request/i);
const repositoryField = (page: Page) => panel(page).getByRole('textbox', { name: /repository di destinazione|destination repository/i });
const plan = (page: Page) => panel(page).getByRole('button', { name: /pianifica la pull request|plan the pull request/i });
const confirmBox = (page: Page) => panel(page).getByRole('checkbox', { name: /ho letto questo piano|i read this plan/i });
const publishButton = (page: Page) => panel(page).getByRole('button', { name: /pubblica la pull request|publish the pull request/i });
const check = (page: Page) => panel(page).getByRole('button', { name: /verifica sul destinatario|check at the destination/i });
const result = (page: Page) => panel(page).getByLabel(/sul destinatario adesso|at the destination now/i);
const enabled = { enabled: true, allowed: ['target-org/orion-api'] };

async function openPanel(page: Page) {
  await open(page);
  await propose(page);
  await expect(panel(page)).toBeVisible();
}

const replacing = () => publicationPlan('orion-api', {
  mode: 'Update', replacesWorkflowSha256: 'ab'.repeat(32),
  pullRequestTitle: 'Add automatic triggers to the GitHub Actions workflow for Azure pipeline #123 (Orion API)',
  findings: [{ code: 'WorkflowWillBeReplaced', severity: 'Info', message: 'The base branch has exactly the workflow approved earlier.' }],
  steps: [
    { code: 'Clone', title: 'Clone the destination repository', command: "git clone 'https://github.com/target-org/orion-api.git' && cd 'orion-api'" },
    { code: 'Replace', title: 'Replace the workflow text with the text shown', command: "cat > '.github/workflows/azdo-123-orion-api.yml'   # paste the workflow, end with Ctrl-D" },
    { code: 'OpenPullRequest', title: 'Open the pull request', command: "gh pr create --base 'main'" }
  ]
});

test('a plan that replaces the file approved earlier says so, names which text, asks for a confirmation that says it, and is published by its hash', async ({ page }) => {
  const store = await setup(page, approved(), { publication: enabled, plan: replacing(), published: publicationRecord({ mode: 'Update', replacedWorkflowSha256: 'ab'.repeat(32), pullNumber: 8,
    pullUrl: 'https://github.com/target-org/orion-api/pull/8' }) });
  await openPanel(page);
  await repositoryField(page).fill('orion-api');
  await plan(page).click();

  await expect(panel(page).getByText(/sostituisce un file|replaces a file/i)).toBeVisible();
  await expect(panel(page).getByText(new RegExp('abababababab…')).first()).toBeVisible(); // which approved text is replaced, by its hash
  await expect(panel(page).getByText(/il branch di base cambia solo quando una persona unisce|base branch changes only when a person merges/i).first()).toBeVisible();
  await expect(panel(page).getByText(/sostituisci il testo del workflow|replace the workflow text/i)).toBeVisible();
  await expect(panel(page).getByText(/Add automatic triggers to the GitHub Actions workflow/)).toBeVisible();
  // the confirmation is not the one for a new file: it says the file is replaced
  await expect(confirmBox(page)).toHaveCount(1);
  await expect(panel(page).getByText(/sostituisce il file già presente|replaces the file already on the base branch/i)).toBeVisible();
  await expect(publishButton(page)).toBeDisabled();
  await confirmBox(page).check();
  await publishButton(page).click();

  await expect(panel(page).getByText(/pull request #8 aperta|pull request #8 opened/i)).toBeVisible();
  expect(store.publishes).toEqual([{ destinationConnectionId: destinationId, repository: 'orion-api', baseBranch: null, planSha256: 'd'.repeat(64) }]);
  await expect(panel(page).locator('.publication-attempts').getByText(/sostituzione|replacement/i)).toBeVisible();
});

test('a plan that adds a new file shows none of the replacement wording', async ({ page }) => {
  await setup(page, approved(), { publication: enabled });
  await openPanel(page);
  await repositoryField(page).fill('orion-api');
  await plan(page).click();
  await expect(panel(page).getByText(/nulla d.intralcio|nothing stands in the way/i)).toBeVisible();
  await expect(panel(page).getByText(/sostituisce un file|replaces a file/i)).toHaveCount(0);
  await expect(panel(page).getByText(/sostituisci il testo del workflow|replace the workflow text/i)).toHaveCount(0);
  await expect(panel(page).getByText(/ho letto questo piano e il testo del workflow\. pubblica|i read this plan and the workflow text\. publish/i)).toBeVisible();
});

test('an attempt can be checked at the destination: the request names the attempt and the connection, and the result is read only', async ({ page }) => {
  const attempt = publicationRecord();
  const store = await setup(page, approved(), { publication: enabled, attempts: [attempt] });
  await openPanel(page);
  await expect(check(page)).toBeVisible();
  expect(store.reconciles).toEqual([]); // nothing is read until a person asks
  await check(page).click();

  await expect(result(page)).toBeVisible();
  await expect(result(page).getByText(/pull request aperta, come scritta|pull request open, as written/i)).toBeVisible();
  await expect(result(page).getByText(/sola lettura|read only/i)).toBeVisible();
  await expect(result(page).getByText(/una persona la rivede|a person reviews it/i)).toBeVisible();
  await expect(result(page).getByText('#7 aperta').or(result(page).getByText('#7 open'))).toBeVisible();
  await expect(result(page).getByText(/il testo approvato|the approved text/i)).toBeVisible();
  await expect(result(page).getByText('eeeeeeeeeeee')).toBeVisible();
  expect(store.reconciles).toEqual([{ publication: attempt.id, body: { destinationConnectionId: destinationId } }]);
  expect(store.publishes).toEqual([]);
});

test('what was left behind by an attempt that may have written is shown with what the product will not do', async ({ page }) => {
  const failed = publicationRecord({ state: 'Failed', failedAtState: 'FileCommitting', errorCode: 'PublicationFileFailed', writesPossible: true, pullNumber: null, pullUrl: null, commitSha: null });
  await setup(page, approved(), { publication: enabled, attempts: [failed], reconcile: reconciliation({
    verdict: 'BranchWithoutPullRequest', ledgerState: 'Failed', ledgerWritesPossible: true, ledgerCommitSha: null, ledgerPullNumber: null,
    findings: [
      { code: 'BranchWithoutPullRequest', severity: 'Warning', message: 'The branch exists and its file is not the approved text, and no pull request uses it.' },
      { code: 'ProductWillNotRetry', severity: 'Info', message: 'The product will not retry this attempt or clean up after it.' }
    ],
    observed: { repositoryExists: true, archived: false, baseTipSha: 'c'.repeat(40), headBranchSha: 'c'.repeat(40), pulls: [], fileOnHead: 'PriorVersion', fileOnBase: 'PriorVersion' }
  }) });
  await openPanel(page);
  await check(page).click();

  await expect(result(page).getByText(/un branch senza pull request|a branch with no pull request/i)).toBeVisible();
  await expect(result(page).getByText(/non aprirà la pull request né cancellerà il branch|will not open the pull request or delete the branch/i)).toBeVisible();
  await expect(result(page).getByText(/non ripeterà questo tentativo|will not retry this attempt/i)).toBeVisible();
  await expect(result(page).getByText(/il testo che è stato sostituito|the text that was replaced/i).first()).toBeVisible();
  await expect(result(page).getByText(/nessuna|none/i)).toBeVisible(); // no pull request
});

test('a merged pull request is called merged and not flagged', async ({ page }) => {
  await setup(page, approved(), { publication: enabled, attempts: [publicationRecord()], reconcile: reconciliation({
    verdict: 'Merged', findings: [{ code: 'BaseMoved', severity: 'Info', message: 'The base branch has moved.' }],
    observed: { repositoryExists: true, archived: false, baseTipSha: 'f'.repeat(40), headBranchSha: null,
      pulls: [{ number: 7, state: 'closed', merged: true, headSha: 'e'.repeat(40), mergeCommitSha: 'f'.repeat(40) }], fileOnHead: 'Absent', fileOnBase: 'Identical' } }) });
  await openPanel(page);
  await check(page).click();
  await expect(result(page).locator('.connection-badge')).toHaveText(/unita|merged/i);
  await expect(result(page).getByText(/il branch di base ha il testo approvato|base branch has the approved text/i)).toBeVisible();
  await expect(result(page).getByText(/#7 unita|#7 merged/i)).toBeVisible();
  await expect(result(page).locator('.connection-badge.invalid')).toHaveCount(0);
});

test('a pull request whose branch or file changed is not presented as safe to merge', async ({ page }) => {
  await setup(page, approved(), { publication: enabled, attempts: [publicationRecord()], reconcile: reconciliation({
    verdict: 'OpenPullRequestChanged', findings: [
      { code: 'BranchMoved', severity: 'Warning', message: 'The branch is not at the commit the product wrote.' },
      { code: 'OpenPullRequestChanged', severity: 'Warning', message: 'The pull request is open, but the file on its branch is not the approved text.' }],
    observed: { repositoryExists: true, archived: false, baseTipSha: 'c'.repeat(40), headBranchSha: '9'.repeat(40),
      pulls: [{ number: 7, state: 'open', merged: false, headSha: '9'.repeat(40), mergeCommitSha: null }], fileOnHead: 'Different', fileOnBase: 'Absent' } }) });
  await openPanel(page);
  await check(page).click();
  await expect(result(page).locator('.connection-badge')).toHaveText(/modificata|changed/i);
  await expect(result(page).getByText(/non unirla credendo|do not merge it believing/i)).toBeVisible();
  await expect(result(page).locator('.connection-badge.invalid')).toHaveCount(1);
  await expect(result(page).getByText(/un file diverso|a different file/i)).toBeVisible();
  await expect(result(page).getByText(/BranchMoved/)).toBeVisible();
});

test('a refusal is explained in words, leaves no stale result, and the attempt can be checked again', async ({ page }) => {
  const store = await setup(page, approved(), { publication: enabled, attempts: [publicationRecord()],
    reconcileRefuse: { status: 409, body: { error: 'DestinationMismatch' }, once: true } });
  await openPanel(page);
  await check(page).click();
  await expect(panel(page).getByRole('alert')).toContainText(/altra organizzazione|another organization/i);
  await expect(result(page)).toHaveCount(0);

  await check(page).click();
  await expect(result(page).getByText(/pull request aperta, come scritta|pull request open, as written/i)).toBeVisible();
  await expect(panel(page).getByRole('alert')).toHaveCount(0);
  expect(store.reconciles).toHaveLength(2);
});

test('an unreadable destination says nothing about what exists and is not shown as a good result', async ({ page }) => {
  await setup(page, approved(), { publication: enabled, attempts: [publicationRecord()], reconcile: reconciliation({
    verdict: 'TargetUnreadable', observed: null, findings: [{ code: 'TargetUnreadable', severity: 'Warning', message: 'The destination could not be read just now.' }] }) });
  await openPanel(page);
  await check(page).click();
  await expect(result(page).getByText(/non si è potuto leggere|could not be read/i).first()).toBeVisible();
  await expect(result(page).locator('.connection-badge.invalid')).toHaveCount(1);
  await expect(result(page).locator('dl')).toHaveCount(0); // no facts are invented
});

test('an earlier pull request still open for the same file blocks the plan, says which one, and a warning about one that could not be read does not', async ({ page }) => {
  const blocked = publicationPlan('orion-api', {
    ready: false,
    findings: [{ code: 'EarlierPullRequestOpen', severity: 'Blocker', message: 'The conversion this proposal was derived from is still open as pull request #7 in this repository (not merged).' }]
  });
  await setup(page, approved(), { publication: enabled, plan: blocked });
  await openPanel(page);
  await repositoryField(page).fill('orion-api');
  await plan(page).click();

  await expect(panel(page).getByText(/pull request #7.*ancora aperta|pull request #7.*still open/i).first()).toBeVisible();
  await expect(panel(page).getByText(/unisci o chiudi la #7|merge or close #7/i)).toBeVisible();
  await expect(panel(page).getByText(/qualcosa è d.intralcio|something stands in the way|bloccat|blocked/i).first()).toBeVisible();
  await expect(publishButton(page)).toHaveCount(0); // nothing to confirm while something stands in the way
});

test('a pull request that could not be read is shown as unknown and the plan can still be confirmed', async ({ page }) => {
  const unchecked = publicationPlan('orion-api', {
    ready: true,
    findings: [{ code: 'EarlierPullRequestUnchecked', severity: 'Warning', message: 'The conversion was published as pull request #7; it could not be read just now.' }]
  });
  await setup(page, approved(), { publication: enabled, plan: unchecked });
  await openPanel(page);
  await repositoryField(page).fill('orion-api');
  await plan(page).click();

  await expect(panel(page).getByText(/pull request #7.*non si sa se sia ancora aperta|pull request #7.*whether it is still open is not known/i)).toBeVisible();
  await expect(confirmBox(page)).toBeVisible();
});

// ---- releasing an attempt that keeps its plan blocked ----------------------------------------------------------------------------------------

const failedWrote = () => publicationRecord({ state: 'Failed', failedAtState: 'FileCommitting', errorCode: 'PublicationFileFailed', writesPossible: true, pullNumber: null, pullUrl: null,
  commitSha: null, blocksItsPlan: true });
const nothingLeft = (overrides: Record<string, unknown> = {}) => reconciliation({
  verdict: 'NothingFound', ledgerState: 'Failed', ledgerWritesPossible: true, ledgerCommitSha: null, ledgerPullNumber: null, ledgerBlocksPlan: true, ledgerReleased: false,
  findings: [
    { code: 'LedgerStillBlocksPlan', severity: 'Info', message: 'Nothing of the attempt is left, but the ledger still blocks this exact plan.' },
    { code: 'NothingFound', severity: 'Info', message: 'Nothing found.' }
  ],
  observed: { repositoryExists: true, archived: false, baseTipSha: 'c'.repeat(40), headBranchSha: null, pulls: [], fileOnHead: 'Absent', fileOnBase: 'Absent' },
  ...overrides
});
const releaseForm = (page: Page) => panel(page).getByLabel(/rilascia questo tentativo|release this attempt/i);
const releaseNote = (page: Page) => releaseForm(page).getByRole('textbox', { name: /cosa è stato ripulito|what was cleaned up/i });
const releaseButton = (page: Page) => releaseForm(page).getByRole('button', { name: /rilascia il tentativo|release the attempt/i });

test('a superadmin releases an attempt the ledger still blocks, with a note, and it is then shown as released and no longer offered', async ({ page }) => {
  const blockedPlan = publicationPlan('orion-api', { ready: false, findings: [{ code: 'AttemptBlocksPlan', severity: 'Blocker', message: 'An earlier attempt of this exact plan failed.' }] });
  const store = await setup(page, approved(), { publication: enabled, plan: blockedPlan, attempts: [failedWrote()], reconcile: nothingLeft(), reconcileAfterRelease: nothingLeft({
    ledgerBlocksPlan: false, ledgerReleased: true, findings: [
      { code: 'AttemptReleased', severity: 'Info', message: 'A person released this attempt.' }, { code: 'NothingFound', severity: 'Info', message: 'Nothing found.' }] }) });
  await openPanel(page);
  await expect(releaseForm(page)).toHaveCount(0); // not before the destination has been read
  await repositoryField(page).fill('orion-api');
  await plan(page).click();
  const keptBlocked = panel(page).getByText(/tiene bloccato \(vedi i tentativi|keeps it blocked \(see the attempts/i);
  await expect(keptBlocked).toBeVisible();
  await check(page).click();

  await expect(result(page).getByText(/Del tentativo non resta nulla|Nothing of the attempt is left/i)).toBeVisible();
  await expect(releaseForm(page)).toBeVisible();
  await expect(releaseButton(page)).toBeDisabled(); // a note is part of it
  await releaseNote(page).fill('ab');
  await expect(releaseButton(page)).toBeDisabled();
  await releaseNote(page).fill('  Deleted the branch from the web page.  ');
  await releaseButton(page).click();

  await expect(panel(page).getByText(/rilasciato da reviewer|released by reviewer/i)).toBeVisible();
  await expect(panel(page).getByText(/Deleted the branch from the web page\./)).toBeVisible();
  await expect(result(page).getByText(/ha rilasciato questo tentativo|released this attempt/i)).toBeVisible();
  await expect(releaseForm(page)).toHaveCount(0);
  await expect(keptBlocked).toHaveCount(0); // the plan that said the attempt was in the way is gone: it is made again, not edited
  // what was sent: the attempt, the connection for its organization and the note as written (the server keeps it trimmed)
  expect(store.releases).toHaveLength(1);
  expect(store.releases[0].publication).toBe(failedWrote().id);
  expect(store.releases[0].body).toEqual({ destinationConnectionId: destinationId, note: 'Deleted the branch from the web page.' });
  expect(store.reconciles).toHaveLength(2); // read again after the release, to show what the ledger says now
});

test('the release is offered to a superadmin only, and only where the product said the ledger still blocks the plan', async ({ page }) => {
  // an admin who may plan and check sees why, but cannot release
  await setup(page, approved(), { publication: enabled, attempts: [failedWrote()], reconcile: nothingLeft(), role: 'admin' });
  await openPanel(page);
  await check(page).click();
  await expect(result(page).getByText(/Del tentativo non resta nulla|Nothing of the attempt is left/i)).toBeVisible();
  await expect(releaseForm(page)).toHaveCount(0);
});

test('nothing is offered to release while the destination still shows something of the attempt', async ({ page }) => {
  await setup(page, approved(), { publication: enabled, attempts: [failedWrote()], reconcile: reconciliation({
    verdict: 'BranchWithoutPullRequest', ledgerState: 'Failed', ledgerWritesPossible: true, ledgerCommitSha: null, ledgerPullNumber: null, ledgerBlocksPlan: true,
    findings: [{ code: 'ProductWillNotRetry', severity: 'Info', message: 'The product will not retry this attempt or clean up after it.' }],
    observed: { repositoryExists: true, archived: false, baseTipSha: 'c'.repeat(40), headBranchSha: 'c'.repeat(40), pulls: [], fileOnHead: 'Absent', fileOnBase: 'Absent' } }) });
  await openPanel(page);
  await check(page).click();
  await expect(result(page).getByText(/non ripeterà questo tentativo|will not retry this attempt/i)).toBeVisible();
  await expect(releaseForm(page)).toHaveCount(0);
});

test('a release refused because something of the attempt is still there says what, leaves the note, and shows what the destination has now', async ({ page }) => {
  const store = await setup(page, approved(), { publication: enabled, attempts: [failedWrote()], reconcile: nothingLeft(),
    releaseRefuse: { status: 409, body: { error: 'PublicationLeftoversExist', details: ['BranchExists', 'PullRequestOpen'] } } });
  await openPanel(page);
  await check(page).click();
  await releaseNote(page).fill('I think it is clean.');
  await releaseButton(page).click();

  const refusal = releaseForm(page).getByRole('alert');
  await expect(refusal).toContainText(/ancora sul destinatario|still at the destination/i);
  await expect(refusal).toContainText(/il branch esiste ancora|the branch still exists/i);
  await expect(refusal).toContainText(/ancora aperta|still open/i);
  await expect(releaseNote(page)).toHaveValue('I think it is clean.'); // what was typed is not lost
  await expect(releaseButton(page)).toBeEnabled();
  expect(store.reconciles).toHaveLength(2); // and the destination is read again to show what is there now
});

test('a release that cannot be checked or was decided meanwhile is explained in words, not as a code', async ({ page }) => {
  for (const [error, status, words] of [
    ['DestinationUnreadable', 422, /non si è potuto leggere|could not be read/i],
    ['PublicationAlreadyReleased', 409, /già stato rilasciato|already released/i],
    ['PublicationStillRunning', 409, /ancora in corso|still be running/i],
    ['NoteRequired', 400, /almeno 3 caratteri|at least 3 characters/i]
  ] as const) {
    await setup(page, approved(), { publication: enabled, attempts: [failedWrote()], reconcile: nothingLeft(), releaseRefuse: { status, body: { error } } });
    await openPanel(page);
    await check(page).click();
    await releaseNote(page).fill('Cleaned it up.');
    await releaseButton(page).click();
    const alert = releaseForm(page).getByRole('alert');
    await expect(alert).toContainText(words);
    await expect(alert).not.toContainText(error);
    await page.unrouteAll({ behavior: 'ignoreErrors' });
  }
});

test('a plan an earlier attempt keeps blocked says so, in words, with nothing to confirm', async ({ page }) => {
  const blocked = publicationPlan('orion-api', { ready: false, findings: [{ code: 'AttemptBlocksPlan', severity: 'Blocker', message: 'An earlier attempt of this exact plan failed.' }] });
  await setup(page, approved(), { publication: enabled, plan: blocked, attempts: [failedWrote()] });
  await openPanel(page);
  await repositoryField(page).fill('orion-api');
  await plan(page).click();

  await expect(panel(page).getByText(/tiene bloccato \(vedi i tentativi|keeps it blocked \(see the attempts/i)).toBeVisible();
  await expect(publishButton(page)).toHaveCount(0);
});

test('an attempt that was released is shown as released from the start, with who and why', async ({ page }) => {
  await setup(page, approved(), { publication: enabled, attempts: [{ ...failedWrote(), blocksItsPlan: false, releasedAtUtc: '2026-11-13T17:00:00Z', releasedBy: 'ops-lead', releaseNote: 'Branch removed, checked by hand.' }] });
  await openPanel(page);
  await expect(panel(page).getByText(/rilasciato da ops-lead|released by ops-lead/i)).toBeVisible();
  await expect(panel(page).getByText(/Branch removed, checked by hand\./)).toBeVisible();
});
