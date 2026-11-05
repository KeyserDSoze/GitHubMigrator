import { expect, test } from '@playwright/test';

const id = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const connectionId = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
const summary = {
  id, connectionId, sourceProvider: 'azure-devops-services', organization: 'demo',
  collectedAtUtc: '2026-10-29T10:00:00Z', completeness: 'partial',
  projectCount: 1, repositoryCount: 2, refCount: 2, identityCount: 1,
  issueCount: 2, identitiesComplete: true, permissionsComplete: false
};
const detail = {
  id, connectionId, sourceProvider: 'azure-devops-services', organization: 'demo',
  collectedAtUtc: summary.collectedAtUtc,
  snapshot: {
    projects: [{ providerId: 'proj-1', name: 'Payments', repositories: [
      { providerId: 'repo-1', projectId: 'proj-1', name: 'Api', versionControl: 0, sourcePath: null },
      { providerId: 'tfvc:proj-1:$/Payments', projectId: 'proj-1', name: 'Payments', versionControl: 1, sourcePath: '$/Payments' }
    ] }],
    identities: [{ providerId: 'aad.example', displayName: 'Alice', email: 'alice@example.invalid' }]
  },
  refs: [
    { repositoryId: 'repo-1', name: 'refs/heads/main', objectId: 'sha1' },
    { repositoryId: 'repo-1', name: 'refs/heads/dev', objectId: 'sha2' }
  ],
  issues: [{ code: 'PermissionsNotCollected', projectId: null, repositoryId: null },
    { code: 'TfvcPathsRequireFullInventory', projectId: 'proj-1', repositoryId: null }],
  identitiesComplete: true, permissionsComplete: false
};

async function mockLogin(page: import('@playwright/test').Page, role: string) {
  await page.route('**/api/auth/login', route => route.fulfill({ json: {
    accessToken: 'mock-access-token', mustChangePassword: false,
    username: role === 'admin' ? 'admin' : 'viewer', role
  } }));
  await page.goto('/');
  await page.getByRole('textbox', { name: /nome utente|username/i }).fill(role);
  await page.getByLabel(/^password$/i).fill('some-test-password');
  await page.getByRole('button', { name: /^accedi$|^sign in$/i }).click();
}

test('admin discovers Git/TFVC, saves snapshot and reviews partial permissions', async ({ page }) => {
  let scanRequests = 0;
  await page.route('**/api/connections', route => route.fulfill({ json: [{
    id: connectionId, name: 'Demo DevOps', providerKey: 'azure-devops-services',
    direction: 'source', organization: 'demo',
    validationCode: 'Verified'
  }] }));
  const discoveryJobId = 'cccccccc-cccc-cccc-cccc-cccccccccccc';
  const queued = {
    id: discoveryJobId, kind: 'DiscoverSource', snapshotId: '00000000-0000-0000-0000-000000000000',
    sourceConnectionId: connectionId, enqueuedBy: 'dddddddd-dddd-dddd-dddd-dddddddddddd',
    state: 'Queued', attempts: 0, maxAttempts: 5,
    availableAtUtc: summary.collectedAtUtc, leaseUntilUtc: null,
    createdAtUtc: summary.collectedAtUtc, updatedAtUtc: summary.collectedAtUtc,
    completedAtUtc: null, lastErrorCode: null, result: null
  };
  await page.route('**/api/discoveries', route => {
    return route.fulfill({ json: scanRequests ? [summary] : [] });
  });
  await page.route('**/api/jobs/discovery', route => {
    expect(route.request().method()).toBe('POST');
    const body = route.request().postDataJSON();
    expect(body.connectionId).toBe(connectionId);
    expect(body.requestId).toMatch(/^[0-9a-f-]{36}$/);
    scanRequests++;
    return route.fulfill({ status: 202, json: queued });
  });
  await page.route(`**/api/jobs/${discoveryJobId}`, route => route.fulfill({ json: {
    ...queued, state: 'Succeeded', attempts: 1, snapshotId: id,
    result: {
      snapshotId: id, sourceConnectionId: connectionId,
      sourceCollectedAtUtc: summary.collectedAtUtc, appliedAtUtc: summary.collectedAtUtc,
      observed: 6, added: 6, changed: 0, unchanged: 0,
      markedMissing: 0, sourceInventoryComplete: false
    }
  } }));
  await page.route(`**/api/jobs/connections/${connectionId}`, route =>
    route.fulfill({ json: scanRequests ? [{
      ...queued, state: 'Succeeded', attempts: 1, snapshotId: id
    }] : [] }));
  await page.route(`**/api/jobs/${discoveryJobId}/ref-manifest`, route =>
    route.fulfill({ json: {
      jobId: discoveryJobId, sourceConnectionId: connectionId,
      assetCount: 2, chunkSize: 250, chunkCount: 1,
      rootSha256: 'a'.repeat(64), preparedAtUtc: summary.collectedAtUtc
    } }));
  await page.route(`**/api/discoveries/${id}`, route => route.fulfill({ json: detail }));
  await mockLogin(page, 'admin');
  await page.getByRole('button', { name: /^discovery$/i }).click();
  await expect(page.getByText(/nessun inventario salvato|no saved inventories/i).first()).toBeVisible();
  await page.getByRole('button', { name: /accoda discovery|queue discovery/i }).click();
  await expect(page.getByRole('heading', { name: 'Payments' })).toBeVisible();
  const manifest = page.getByRole('status', {
    name: /manifest immutabile dei riferimenti git|immutable git ref manifest/i
  });
  await expect(manifest).toContainText('2');
  await expect(manifest).toContainText('1');
  await expect(page.getByText('Api', { exact: true })).toBeVisible();
  await expect(page.getByText('TFVC', { exact: true })).toBeVisible();
  await expect(page.getByText(/permessi effettivi|effective repository permissions/i).first()).toBeVisible();
  await page.getByRole('button', { name: /^identità$|^identities$/i }).click();
  await expect(page.getByText('Alice', { exact: true }).first()).toBeVisible();
  await page.getByRole('button', { name: /^(avvisi di copertura|coverage warnings)$/i }).click();
  await expect(page.getByText(/PermissionsNotCollected/i)).toBeVisible();
  expect(scanRequests).toBe(1);
});


test('externalized ref snapshot displays real total without inventing zero refs per repo', async ({ page }) => {
  const compact = { ...summary, refsExternalized: true, refCount: 1250 };
  const external = { ...detail, refs: [], refsExternalized: true, refCount: 1250 };
  await page.route('**/api/connections', route => route.fulfill({ json: [{
    id: connectionId, name: 'Demo DevOps', providerKey: 'azure-devops-services',
    direction: 'source', organization: 'demo', validationCode: 'Verified'
  }] }));
  await page.route('**/api/discoveries', route => route.fulfill({ json: [compact] }));
  await page.route(`**/api/discoveries/${id}`, route =>
    route.fulfill({ json: external }));
  await page.route(`**/api/jobs/connections/${connectionId}`, route =>
    route.fulfill({ json: [] }));
  await mockLogin(page, 'admin');
  await page.getByRole('button', { name: /^discovery$/i }).click();
  await expect(page.getByRole('heading', { name: 'Payments' })).toBeVisible();
  await expect(page.getByText('1250', { exact: true })).toBeVisible();
  await expect(page.getByRole('note')).toContainText(/PostgreSQL/);
  await expect(page.getByText(/riferimenti nel manifest postgresql|refs in postgresql manifest/i))
    .toBeVisible();
  await expect(page.getByText(/^0 (riferimenti|refs)$/i)).toHaveCount(0);
});

test('discovery worker survives page refresh while no snapshot has been saved', async ({ page }) => {
  const discoveryJobId = 'eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee';
  await page.route('**/api/connections', route => route.fulfill({ json: [{
    id: connectionId, name: 'Demo DevOps', providerKey: 'azure-devops-services',
    direction: 'source', organization: 'demo', validationCode: 'Verified'
  }] }));
  await page.route('**/api/discoveries', route => route.fulfill({ json: [] }));
  await page.route(`**/api/jobs/connections/${connectionId}`, route => route.fulfill({ json: [{
    id: discoveryJobId, kind: 'DiscoverSource',
    snapshotId: '00000000-0000-0000-0000-000000000000',
    sourceConnectionId: connectionId, state: 'Queued', attempts: 0, maxAttempts: 5,
    availableAtUtc: summary.collectedAtUtc, leaseUntilUtc: null,
    createdAtUtc: summary.collectedAtUtc, updatedAtUtc: summary.collectedAtUtc,
    completedAtUtc: null, lastErrorCode: null, result: null
  }] }));
  await page.route(`**/api/jobs/${discoveryJobId}`, route => route.fulfill({ json: {
    id: discoveryJobId, kind: 'DiscoverSource',
    snapshotId: '00000000-0000-0000-0000-000000000000',
    sourceConnectionId: connectionId, state: 'Running', attempts: 1, maxAttempts: 5,
    availableAtUtc: summary.collectedAtUtc, leaseUntilUtc: summary.collectedAtUtc,
    createdAtUtc: summary.collectedAtUtc, updatedAtUtc: summary.collectedAtUtc,
    completedAtUtc: null, lastErrorCode: null, result: null
  } }));
  await page.route(`**/api/jobs/${discoveryJobId}/ref-progress`, route =>
    route.fulfill({ json: {
      jobId: discoveryJobId, checkpointedRefPages: 3, observedRefs: 2450
    } }));
  await page.route(`**/api/jobs/${discoveryJobId}/identity-progress`, route =>
    route.fulfill({ json: {
      jobId: discoveryJobId, checkpointedUserPages: 2, observedUsers: 1001
    } }));
  await mockLogin(page, 'admin');
  await page.getByRole('button', { name: /^discovery$/i }).click();
  const status = page.getByRole('status', { name: /job di staging persistente|durable staging job/i });
  await expect(status).toContainText(/in coda|queued/i);
  await expect(status).toContainText(/in esecuzione|running/i, { timeout: 15000 });
  await expect(page.getByText(/nessun inventario salvato|no saved inventories/i).first()).toBeVisible();
  const progress = page.getByRole('status', {
    name: /riferimenti git osservati in postgresql|git refs observed in postgresql/i
  });
  await expect(progress).toContainText('2450', { timeout: 15000 });
  await expect(progress).toContainText('3');
  const graph = page.getByRole('status', {
    name: /identità graph osservate in postgresql|graph identities observed in postgresql/i
  });
  await expect(graph).toContainText('1001', { timeout: 15000 });
  await expect(graph).toContainText('2');
  await expect(graph).toContainText(/provvisor|provisional/i);
});

test('regular platform user cannot discover or read snapshots', async ({ page }) => {
  let requests = 0;
  await page.route('**/api/discoveries**', route => { requests++; return route.fulfill({ status: 403 }); });
  await page.route('**/api/connections', route => { requests++; return route.fulfill({ status: 403 }); });
  await mockLogin(page, 'user');
  await page.getByRole('button', { name: /^discovery$/i }).click();
  await expect(page.getByText(/solo gli amministratori|only platform administrators/i)).toBeVisible();
  expect(requests).toBe(0);
});

test('admin inspects Graph group edges and raw allow/deny masks without effective grant claims', async ({ page }) => {
  const summaryWithSecurity = {
    ...summary, groupCount: 1, membershipCount: 1, aclEntryCount: 2,
    securityCoverage: {
      groupsComplete: true, membershipsComplete: true,
      aclsCollected: true, effectivePermissionsVerified: false
    }
  };
  const detailWithSecurity = {
    ...detail,
    snapshot: {
      ...detail.snapshot,
      identities: [{ ...detail.snapshot.identities[0], legacyDescriptor: 'legacy-alice' }]
    },
    groups: [{ descriptor: 'vssgp.team', displayName: 'Contributors', legacyDescriptor: 'legacy-group' }],
    memberships: [{ groupDescriptor: 'vssgp.team', memberDescriptor: 'aad.example' }],
    aclEvidence: [
      { repositoryId: 'repo-1', securityToken: 'repoV2/proj-1', securityDescriptor: 'legacy-group',
        allowMask: 2, denyMask: 0, inheritPermissions: true, scope: 'project' },
      { repositoryId: 'repo-1', securityToken: 'repoV2/proj-1/repo-1', securityDescriptor: 'legacy-alice',
        allowMask: 4, denyMask: 2, inheritPermissions: false, scope: 'repository' }
    ],
    securityCoverage: summaryWithSecurity.securityCoverage,
    issues: [{ code: 'EffectivePermissionsNotVerified', projectId: null, repositoryId: null }]
  };
  await page.route('**/api/connections', route => route.fulfill({ json: [{
    id: connectionId, name: 'Demo DevOps', providerKey: 'azure-devops-services',
    direction: 'source', organization: 'demo'
  }] }));
  await page.route('**/api/discoveries', route => route.fulfill({ json: [summaryWithSecurity] }));
  await page.route(`**/api/discoveries/${id}`, route => route.fulfill({ json: detailWithSecurity }));
  await mockLogin(page, 'admin');
  await page.getByRole('button', { name: /^discovery$/i }).click();
  await page.getByRole('button', { name: /^gruppi e acl$|^groups and acl evidence$/i }).click();
  await expect(page.getByText('Contributors', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('Alice', { exact: true }).first()).toBeVisible();
  await expect(page.getByText(/ACL di progetto|Project ACL/i)).toBeVisible();
  await expect(page.getByText(/ACL repository|Repository ACL/i)).toBeVisible();
  await expect(page.getByText(/consenti 4.*nega 2|allow 4.*deny 2/i)).toBeVisible();
  await expect(page.getByText(/ACL grezze non equivalgono|Raw ACLs are not effective/i).first()).toBeVisible();
});

test('admin replays a source snapshot into PostgreSQL staging and sees changed-asset counts', async ({ page }) => {
  let syncCalls = 0;
  await page.route('**/api/connections', route => route.fulfill({ json: [{
    id: connectionId, name: 'Demo DevOps', providerKey: 'azure-devops-services',
    direction: 'source', organization: 'demo', validationCode: 'Verified'
  }] }));
  await page.route('**/api/discoveries', route => route.fulfill({ json: [summary] }));
  await page.route(`**/api/discoveries/${id}`, route => route.fulfill({ json: detail }));
  await page.route(`**/api/staging/snapshots/${id}`, route =>
    route.fulfill({ status: 404, json: { error: 'StagingSyncNotFound' } }));
  await page.route(`**/api/staging/snapshots/${id}/sync`, route => {
    expect(route.request().method()).toBe('POST');
    syncCalls++;
    return route.fulfill({ json: {
      snapshotId: id, sourceConnectionId: connectionId, sourceCollectedAtUtc: summary.collectedAtUtc,
      appliedAtUtc: summary.collectedAtUtc, observed: 6, added: 5, changed: 1,
      unchanged: 0, markedMissing: 0, sourceInventoryComplete: false
    } });
  });
  await mockLogin(page, 'admin');
  await page.getByRole('button', { name: /^discovery$/i }).click();
  await expect(page.getByRole('heading', { name: 'Payments' })).toBeVisible();
  await page.getByRole('button', { name: /sincronizza snapshot nel db staging|sync selected snapshot to staging/i }).click();
  const status = page.getByRole('status', { name: /db intermedio di staging|intermediate staging db/i });
  await expect(status).toContainText(/6/);
  await expect(status).toContainText(/5/);
  await expect(status).toContainText(/copertura parziale|partial or unverified coverage/i);
  expect(syncCalls).toBe(1);
});

test('admin queues a durable staging job and sees persisted success without claiming GitHub migration', async ({ page }) => {
  const jobId = 'cccccccc-cccc-cccc-cccc-cccccccccccc';
  let submissions = 0;
  let polls = 0;
  const baseJob = {
    id: jobId, kind: 'StageSnapshot', snapshotId: id, sourceConnectionId: connectionId,
    enqueuedBy: 'dddddddd-dddd-dddd-dddd-dddddddddddd',
    state: 'Queued', attempts: 0, maxAttempts: 5,
    availableAtUtc: summary.collectedAtUtc, leaseUntilUtc: null,
    createdAtUtc: summary.collectedAtUtc, updatedAtUtc: summary.collectedAtUtc,
    completedAtUtc: null, lastErrorCode: null, result: null
  };
  const sync = {
    snapshotId: id, sourceConnectionId: connectionId,
    sourceCollectedAtUtc: summary.collectedAtUtc, appliedAtUtc: summary.collectedAtUtc,
    observed: 5, added: 5, changed: 0, unchanged: 0,
    markedMissing: 0, sourceInventoryComplete: false
  };
  await page.route('**/api/connections', route => route.fulfill({ json: [{
    id: connectionId, name: 'Demo DevOps', providerKey: 'azure-devops-services',
    direction: 'source', organization: 'demo', validationCode: 'Verified'
  }] }));
  await page.route('**/api/discoveries', route => route.fulfill({ json: [summary] }));
  await page.route(`**/api/discoveries/${id}`, route => route.fulfill({ json: detail }));
  await page.route(`**/api/staging/snapshots/${id}`, route =>
    route.fulfill({ status: 404, json: { error: 'StagingSyncNotFound' } }));
  await page.route(`**/api/jobs/connections/${connectionId}`, route =>
    route.fulfill({ json: [] }));
  await page.route('**/api/jobs/staging', route => {
    expect(route.request().method()).toBe('POST');
    expect(route.request().postDataJSON()).toEqual({ snapshotId: id });
    submissions++;
    return route.fulfill({ status: 202, json: baseJob });
  });
  await page.route(`**/api/jobs/${jobId}`, route => {
    polls++;
    return route.fulfill({ json: polls < 2 ? baseJob : {
      ...baseJob, state: 'Succeeded', attempts: 1,
      completedAtUtc: summary.collectedAtUtc, result: sync
    } });
  });
  await mockLogin(page, 'admin');
  await page.getByRole('button', { name: /^discovery$/i }).click();
  await expect(page.getByRole('heading', { name: 'Payments' })).toBeVisible();
  await page.getByRole('button', { name: /accoda sync staging in background|queue background staging sync/i }).click();
  const status = page.getByRole('status', { name: /job di staging persistente|durable staging job/i });
  await expect(status).toContainText(/in coda|queued/i);
  await expect(status).toContainText(/staging completato|succeeded in staging/i, { timeout: 15000 });
  await expect(page.getByRole('status', { name: /db intermedio di staging|intermediate staging db/i }))
    .toContainText('5');
  expect(submissions).toBe(1);
  expect(polls).toBeGreaterThanOrEqual(2);
});

