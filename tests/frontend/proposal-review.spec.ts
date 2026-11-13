import { expect, test } from '@playwright/test';
import { approve, detail, note, open, propose, reject, review, revoke, setup, sha } from './proposal-fixtures';

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
  await expect(page.getByText(/approvare non pubblica nulla|approving publishes nothing/i)).toBeVisible();
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
