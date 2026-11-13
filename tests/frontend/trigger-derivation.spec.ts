import { expect, test } from '@playwright/test';
import { approve, derivedId, derivedSha, deriveButton, detail, note, open, proposalId, propose, review, setup, sha, triggersDetail } from './proposal-fixtures';

const approved = () => detail({ state: 'Approved', approvedSha256: sha, canApprove: false, findings: [] });

test('an approved conversion offers the Azure triggers as a separate proposal and says what it does and does not do', async ({ page }) => {
  const store = await setup(page, approved());
  await open(page);
  await propose(page);

  const section = page.getByLabel(/^trigger azure$|^azure triggers$/i);
  await expect(section).toBeVisible();
  await expect(section.getByText(/nessun modello ai lo vede|no ai model sees it/i)).toBeVisible();
  await expect(section.getByText(/va approvata a parte|approved on its own/i)).toBeVisible();
  expect(store.derives).toEqual([]); // nothing is read until a person asks
  await deriveButton(page).click();

  expect(store.derives).toEqual([proposalId]);
  // the derived proposal replaces the one on screen: its own hash, its own mapping, marked as triggers
  await expect(review(page).getByText(derivedSha)).toBeVisible();
  const mapped = page.getByLabel(/^trigger automatici$|^automatic triggers$/i);
  await expect(mapped.getByText('push — branches: main, release/**')).toBeVisible();
  await expect(mapped.getByText('schedule: 0 3 * * 1-5')).toBeVisible();
  await expect(page.getByText(/parte da solo|starts by itself/i).first()).toBeVisible();
  // and the conversion it came from is listed too, with the kind of each
  await expect(page.locator('.proposal-history').getByText(/^trigger azure$|^azure triggers$/i)).toHaveCount(1);
  await expect(deriveButton(page)).toHaveCount(0); // triggers are never derived from triggers
});

test('every difference from Azure is a warning to accept by name before the derived proposal can be approved', async ({ page }) => {
  const store = await setup(page, approved());
  await open(page);
  await propose(page);
  await deriveButton(page).click();

  await expect(approve(page)).toBeDisabled();
  await page.getByRole('checkbox', { name: /TRIGGER_PR_AZURE_REPOS_IGNORED/ }).check();
  await expect(approve(page)).toBeDisabled(); // one of two is not enough
  await page.getByRole('checkbox', { name: /TRIGGER_STARTS_PRIVILEGED_JOBS/ }).check();
  await expect(approve(page)).toBeEnabled();
  await expect(page.getByText(/dopo che i gate sono stati rieseguiti con la policy dei trigger|run again under the trigger policy/i)).toBeVisible();
  await note(page).fill('Read the triggers and what differs.');
  await approve(page).click();

  await expect(page.getByText(/approvata da reviewer|approved by reviewer/i)).toBeVisible();
  await expect(page.getByText(/trigger compresi|triggers included/i)).toBeVisible();
  expect(store.posts).toEqual([{ verb: 'approve', body: {
    workflowSha256: derivedSha, acknowledgedWarnings: ['TRIGGER_PR_AZURE_REPOS_IGNORED', 'TRIGGER_STARTS_PRIVILEGED_JOBS'], note: 'Read the triggers and what differs.' } }]);
  await expect(deriveButton(page)).toHaveCount(0);
});

test('a derived proposal links back to the approved conversion it came from', async ({ page }) => {
  await setup(page, approved());
  await open(page);
  await propose(page);
  await deriveButton(page).click();
  await expect(review(page).getByText(derivedSha)).toBeVisible();

  await page.getByRole('button', { name: /apri la conversione approvata|open the approved conversion/i }).click();
  await expect(review(page).getByText(sha)).toBeVisible();
  await expect(deriveButton(page)).toBeVisible(); // the conversion offers it again; each request is a new proposal
});

test('a refusal is explained in words and nothing replaces the proposal on screen', async ({ page }) => {
  const store = await setup(page, approved(), { deriveRefuse: { status: 409, body: { error: 'NoAutomaticTriggers' } } });
  await open(page);
  await propose(page);
  await deriveButton(page).click();

  await expect(page.getByRole('alert')).toContainText(/non ha trigger automatici|no automatic trigger to convert/i);
  await expect(review(page).getByText(sha)).toBeVisible();
  await expect(deriveButton(page)).toBeEnabled();
  expect(store.derives).toEqual([proposalId]);
});

test('a conversion that is not approved yet does not offer the triggers', async ({ page }) => {
  await setup(page, detail());
  await open(page);
  await propose(page);
  await expect(review(page)).toBeVisible();
  await expect(deriveButton(page)).toHaveCount(0);
});

test('a kept derived proposal opened from the history shows its mapping, its kind and no second derivation', async ({ page }) => {
  const kept = triggersDetail(proposalId, { state: 'Approved', approvedSha256: derivedSha, canApprove: false });
  await setup(page, approved(), { history: [kept] });
  await open(page);
  await page.getByRole('button', { name: /^apri$|^open$/i }).nth(1).click();

  await expect(review(page).getByText(derivedSha)).toBeVisible();
  await expect(page.getByLabel(/^trigger automatici$|^automatic triggers$/i).getByText('pull_request — branches: main')).toBeVisible();
  await expect(deriveButton(page)).toHaveCount(0);
});
