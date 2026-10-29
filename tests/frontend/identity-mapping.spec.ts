import { expect, test, type Page } from '@playwright/test';

const snapshotId = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const destinationId = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
const sourceDetail = {
  id: snapshotId, connectionId: 'cccccccc-cccc-cccc-cccc-cccccccccccc',
  organization: 'source', collectedAtUtc: '2026-10-29T10:00:00Z',
  snapshot: {
    projects: [], identities: [
      { providerId: 'aad.alice', displayName: 'Alice Smith', email: 'alice@example.invalid' },
      { providerId: 'aad.bob', displayName: 'Bob Smith', email: null }
    ]
  },
  refs: [], issues: [], identitiesComplete: true, permissionsComplete: false
};
const review = (revision: number, mappings: { sourceIdentityId: string; gitHubLogin: string }[]) => ({
  draft: {
    snapshotId, destinationConnectionId: destinationId, targetOrganization: 'gh-target',
    revision, updatedAtUtc: '2026-10-29T10:00:00Z', mappings
  },
  preview: {
    discoveredIdentities: 2, mappedIdentities: mappings.length,
    unmappedIdentities: 2 - mappings.length, unresolvedAclPrincipals: 1,
    sourceIdentitiesComplete: true, effectivePermissionsVerified: false,
    targetAccountsVerified: false, readyForGrants: false,
    issues: [
      { code: 'EffectivePermissionsUnverified', sourceIdentityId: null },
      { code: 'TargetMembershipUnverified', sourceIdentityId: null },
      { code: 'AclPrincipalUnresolved', sourceIdentityId: null }
    ]
  }
});

async function signIn(page: Page, role: string) {
  await page.route('**/api/auth/login', route => route.fulfill({ json: {
    accessToken: 'test-session', mustChangePassword: false,
    username: 'person', role
  } }));
  await page.goto('/');
  await page.getByRole('textbox', { name: /nome utente|username/i }).fill('person');
  await page.getByLabel(/^password$/i).fill('local-password');
  await page.getByRole('button', { name: /^accedi$|^sign in$/i }).click();
}

test('admin maps identities in saved revision and review remains blocked', async ({ page }) => {
  let savedRevision = 0;
  let savedMappings: { sourceIdentityId: string; gitHubLogin: string }[] = [];
  let grants = 0;
  await page.route('**/api/connections', route => route.fulfill({ json: [
    { id: destinationId, name: 'Enterprise destination', organization: 'gh-target',
      providerKey: 'github-enterprise-cloud', direction: 'destination', validationCode: 'Verified' }
  ] }));
  await page.route('**/api/discoveries', route => route.fulfill({ json: [
    { id: snapshotId, organization: 'source', collectedAtUtc: sourceDetail.collectedAtUtc,
      projectCount: 0, repositoryCount: 0, identityCount: 2, refCount: 0,
      completeness: 'partial', issueCount: 0, identitiesComplete: true, permissionsComplete: false }
  ] }));
  await page.route(`**/api/discoveries/${snapshotId}`, route => route.fulfill({ json: sourceDetail }));
  await page.route(`**/api/identity-mappings/${snapshotId}**`, route => {
    if (route.request().method() === 'PUT') {
      const request = route.request().postDataJSON();
      expect(request.destinationConnectionId).toBe(destinationId);
      expect(request.expectedRevision).toBe(savedRevision);
      savedRevision++;
      savedMappings = request.mappings;
      return route.fulfill({ json: review(savedRevision, savedMappings) });
    }
    return route.fulfill({ json: review(savedRevision, savedMappings) });
  });
  await page.route('**/api/connections/*/credential', route => { grants++; return route.fulfill({ status: 403 }); });
  await signIn(page, 'admin');
  await page.getByRole('button', { name: /mapping utenti|identity mapping/i }).click();
  await expect(page.getByText('Alice Smith', { exact: true })).toBeVisible();
  await page.getByRole('textbox', { name: 'Alice Smith GitHub' }).fill('alice-gh');
  await page.getByRole('button', { name: /salva bozza mapping|save mapping draft/i }).click();
  await expect(page.getByRole('status').getByText(/bozza salvata|mapping draft saved/i)).toBeVisible();
  expect(savedRevision).toBe(1);
  expect(savedMappings).toEqual([{ sourceIdentityId: 'aad.alice', gitHubLogin: 'alice-gh' }]);
  await expect(page.getByText(/non pronto per assegnare accessi|not ready to grant access/i)).toBeVisible();
  await expect(page.getByText(/permessi effettivi|effective repository permissions/i).first()).toBeVisible();
  expect(grants).toBe(0);
});

test('duplicate GitHub login is rejected before PUT', async ({ page }) => {
  let putCount = 0;
  await page.route('**/api/connections', route => route.fulfill({ json: [{
    id: destinationId, name: 'Enterprise destination', organization: 'gh-target',
    providerKey: 'github-enterprise-cloud', direction: 'destination', validationCode: 'Verified'
  }] }));
  await page.route('**/api/discoveries', route => route.fulfill({ json: [{
    id: snapshotId, organization: 'source', collectedAtUtc: sourceDetail.collectedAtUtc
  }] }));
  await page.route(`**/api/discoveries/${snapshotId}`, route => route.fulfill({ json: sourceDetail }));
  await page.route(`**/api/identity-mappings/${snapshotId}**`, route => {
    if (route.request().method() === 'PUT') putCount++;
    return route.fulfill({ json: review(0, []) });
  });
  await signIn(page, 'admin');
  await page.getByRole('button', { name: /mapping utenti|identity mapping/i }).click();
  await page.getByRole('textbox', { name: 'Alice Smith GitHub' }).fill('some-gh');
  await page.getByRole('textbox', { name: 'Bob Smith GitHub' }).fill('SOME-GH');
  await page.getByRole('button', { name: /salva bozza mapping|save mapping draft/i }).click();
  await expect(page.getByText(/nomi account GitHub|GitHub account names/i)).toBeVisible();
  expect(putCount).toBe(0);
});

test('regular user has no access to identity mapping requests', async ({ page }) => {
  let calls = 0;
  await page.route('**/api/identity-mappings/**', route => { calls++; return route.fulfill({ status: 403 }); });
  await page.route('**/api/discoveries', route => { calls++; return route.fulfill({ status: 403 }); });
  await page.route('**/api/connections', route => { calls++; return route.fulfill({ status: 403 }); });
  await signIn(page, 'user');
  await page.getByRole('button', { name: /mapping utenti|identity mapping/i }).click();
  await expect(page.getByText(/solo gli amministratori|only platform administrators/i)).toBeVisible();
  expect(calls).toBe(0);
});
