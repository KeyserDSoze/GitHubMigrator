import { expect, test, type Page } from '@playwright/test';

const snapshotId = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const destinationId = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
const source = {
  id: snapshotId, organization: 'az-org', collectedAtUtc: '2026-11-03T11:00:00Z',
  snapshot: { projects: [{
    providerId: 'project', name: 'Payments', repositories: [
      { providerId: 'repo-1', projectId: 'project', name: 'Api', versionControl: 0, sourcePath: null },
      { providerId: 'tfvc-1', projectId: 'project', name: 'Legacy', versionControl: 1, sourcePath: '$/Payments' }
    ]
  }], identities: [] },
  refs: [{ repositoryId: 'repo-1', name: 'refs/heads/main', objectId: 'abc' }],
  issues: [], identitiesComplete: true, permissionsComplete: false
};

async function signIn(page: Page, role: string) {
  await page.route('**/api/auth/login', route => route.fulfill({ json: {
    accessToken: 'test-session', mustChangePassword: false, username: 'operator', role
  } }));
  await page.goto('/');
  await page.getByRole('textbox', { name: /nome utente|username/i }).fill('operator');
  await page.getByLabel(/^password$/i).fill('test-password');
  await page.getByRole('button', { name: /^accedi$|^sign in$/i }).click();
}

test('admin configures a plan and sees blocking read-only preview', async ({ page }) => {
  let previewCount = 0;
  await page.route('**/api/connections', route => route.fulfill({ json: [{
    id: destinationId, name: 'GitHub', providerKey: 'github-enterprise-cloud',
    direction: 'destination', organization: 'gh-org', validationCode: 'Verified'
  }] }));
  await page.route('**/api/discoveries', route => route.fulfill({ json: [{
    id: snapshotId, organization: 'az-org', collectedAtUtc: source.collectedAtUtc
  }] }));
  await page.route(`**/api/discoveries/${snapshotId}`, route => route.fulfill({ json: source }));
  await page.route(`**/api/migration-plans/drafts/${snapshotId}**`, route =>
    route.fulfill({ json: {
      snapshotId, destinationConnectionId: destinationId, targetOrganization: 'gh-org',
      revision: 0, updatedAtUtc: '2026-11-03T11:00:00Z', repositories: []
    } }));
  await page.route('**/api/migration-plans/preview', route => {
    const body = route.request().postDataJSON();
    expect(route.request().method()).toBe('POST');
    expect(body.snapshotId).toBe(snapshotId);
    expect(body.destinationConnectionId).toBe(destinationId);
    expect(body.repositories.filter((r: { include: boolean }) => r.include)).toHaveLength(2);
    previewCount++;
    return route.fulfill({ json: {
      snapshotId, destinationConnectionId: destinationId, targetOrganization: 'gh-org',
      identityDraftRevision: 0, selectedRepositories: 2, availableRepositories: 2,
      effectivePermissionsVerified: false, targetRepositoryAvailabilityVerified: false,
      targetMembershipVerified: false, readyForApproval: false,
      repositories: [
        { sourceRepositoryId: 'repo-1', sourceProjectId: 'project', sourceName: 'Api',
          targetRepositoryName: 'Payments', versionControl: 0, strategy: 0,
          refCount: 1, candidateGitHubLogins: [] },
        { sourceRepositoryId: 'tfvc-1', sourceProjectId: 'project', sourceName: 'Legacy',
          targetRepositoryName: 'Payments', versionControl: 1, strategy: 3,
          refCount: 0, candidateGitHubLogins: [] }
      ],
      checks: [
        { code: 'DuplicateTargetRepositoryName', severity: 'blocker', repositoryId: 'tfvc-1' },
        { code: 'EffectivePermissionsUnverified', severity: 'blocker', repositoryId: null },
        { code: 'TargetRepositoryAvailabilityUnverified', severity: 'blocker', repositoryId: null }
      ]
    } });
  });
  await signIn(page, 'admin');
  await page.getByRole('button', { name: /piano di migrazione|migration plan/i }).click();
  await expect(page.getByText('Api', { exact: true }).first()).toBeVisible();
  await page.getByRole('checkbox').nth(0).check();
  await page.getByRole('checkbox').nth(1).check();
  await page.getByRole('textbox', { name: /nome repository github: legacy|github target name: legacy/i }).fill('Payments');
  await page.getByRole('textbox', { name: /nome repository github: api|github target name: api/i }).fill('Payments');
  await page.getByRole('button', { name: /verifica anteprima piano|validate plan preview/i }).click();
  await expect(page.getByText(/due repository selezionati|two selected repositories/i)).toBeVisible();
  await expect(page.getByText(/non approvato|not approved/i)).toBeVisible();
  expect(previewCount).toBe(1);
});


test('admin can save a versioned plan and a stale save reports a 409 conflict', async ({ page }) => {
  let revision = 0;
  let saves = 0;
  let remoteSelections: { sourceRepositoryId: string; targetRepositoryName: string; strategy: number; include: boolean }[] = [];
  await page.route('**/api/connections', route => route.fulfill({ json: [{
    id: destinationId, name: 'GitHub', providerKey: 'github-enterprise-cloud',
    direction: 'destination', organization: 'gh-org', validationCode: 'Verified'
  }] }));
  await page.route('**/api/discoveries', route => route.fulfill({ json: [{
    id: snapshotId, organization: 'az-org', collectedAtUtc: source.collectedAtUtc
  }] }));
  await page.route(`**/api/discoveries/${snapshotId}`, route => route.fulfill({ json: source }));
  await page.route(`**/api/migration-plans/drafts/${snapshotId}**`, route => {
    if (route.request().method() === 'PUT') {
      const body = route.request().postDataJSON();
      saves++;
      expect(body.destinationConnectionId).toBe(destinationId);
      if (saves === 2) {
        revision = 2;
        remoteSelections = body.repositories.map((s: typeof remoteSelections[number]) =>
          s.sourceRepositoryId === 'repo-1' ? { ...s, targetRepositoryName: 'remote-version' } : s);
        return route.fulfill({ status: 409, json: { error: 'PlanRevisionConflict' } });
      }
      expect(body.expectedRevision).toBe(0);
      revision = 1;
      remoteSelections = body.repositories;
    }
    return route.fulfill({ json: {
      snapshotId, destinationConnectionId: destinationId, targetOrganization: 'gh-org',
      revision, updatedAtUtc: '2026-11-03T11:00:00Z', repositories: remoteSelections
    } });
  });
  await signIn(page, 'admin');
  await page.getByRole('button', { name: /piano di migrazione|migration plan/i }).click();
  const input = page.getByRole('textbox', { name: /nome repository github: api|github target name: api/i });
  await expect(input).toBeVisible();
  await page.getByRole('checkbox').first().check();
  await input.fill('new-api');
  await page.getByRole('button', { name: /^salva bozza$|^save draft$/i }).click();
  await expect(page.getByText(/bozza del piano salvata|migration plan draft saved/i)).toBeVisible();
  await expect(page.getByText(/revisione 1|revision 1/i)).toBeVisible();
  await input.fill('another-name');
  await page.getByRole('button', { name: /^salva bozza$|^save draft$/i }).click();
  await expect(page.getByRole('alert').getByText(/un altro amministratore|another administrator/i)).toBeVisible();
  await page.getByRole('button', { name: /ricarica bozza salvata|reload saved draft/i }).click();
  await expect(input).toHaveValue('remote-version');
  await expect(page.getByText(/revisione 2|revision 2/i)).toBeVisible();
  expect(saves).toBe(2);
});

test('ordinary users cannot access plans or trigger preview requests', async ({ page }) => {
  let calls = 0;
  await page.route('**/api/discoveries**', route => { calls++; return route.fulfill({ status: 403 }); });
  await page.route('**/api/connections', route => { calls++; return route.fulfill({ status: 403 }); });
  await page.route('**/api/migration-plans/**', route => { calls++; return route.fulfill({ status: 403 }); });
  await signIn(page, 'user');
  await page.getByRole('button', { name: /piano di migrazione|migration plan/i }).click();
  await expect(page.getByText(/solo gli amministratori|only platform administrators/i)).toBeVisible();
  expect(calls).toBe(0);
});
