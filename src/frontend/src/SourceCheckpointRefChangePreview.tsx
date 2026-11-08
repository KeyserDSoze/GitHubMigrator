import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  api, type SourceCheckpointRefChange, type SourceCheckpointRefChangePage,
  type SourceSyncCheckpointReceipt
} from './api';

type Props = {
  receipt: SourceSyncCheckpointReceipt;
  token: string; preparationId: string; planId: string;
};

const validSha = (s: unknown): s is string =>
  typeof s === 'string' && /^[0-9a-f]{64}$/.test(s);

function verifyPage(page: SourceCheckpointRefChangePage,
  receipt: SourceSyncCheckpointReceipt, after: string | null) {
  if (receipt.evidenceVersion !== 2 ||
      page.checkpointId !== receipt.id ||
      page.snapshotId !== receipt.proof.snapshotId ||
      page.refManifestSha256 !== receipt.refChangeManifest?.evidenceSha256 ||
      page.refAttestationSha256 !== receipt.refChangeAttestationSha256 ||
      !validSha(page.refManifestSha256) ||
      !validSha(page.refAttestationSha256) ||
      page.totalRefChanges !== receipt.proof.totalRefChanges ||
      page.canPush || page.canRetry || page.canDispatch ||
      page.migrationComplete ||
      !Array.isArray(page.items) || page.items.length > 50 ||
      (page.nextAfter !== null &&
        (page.items.length === 0 ||
         page.nextAfter !== page.items[page.items.length - 1].sourceId)))
    throw new Error('SourceSyncCheckpointUnsafeResponse');

  let previous = after;
  for (const change of page.items) {
    if (typeof change.sourceId !== 'string' ||
        typeof change.sourceRepositoryId !== 'string' ||
        typeof change.refName !== 'string' ||
        !change.refName.startsWith('refs/') ||
        (previous !== null && change.sourceId <= previous) ||
        !Number.isSafeInteger(change.revision) || change.revision < 1 ||
        !['Added', 'Changed', 'Missing', 'Restored'].includes(change.kind) ||
        (change.previousFingerprint !== null &&
         !validSha(change.previousFingerprint)) ||
        (change.currentFingerprint !== null &&
         !validSha(change.currentFingerprint)) ||
        !(
          (change.kind === 'Added' && change.previousFingerprint === null &&
           validSha(change.currentFingerprint)) ||
          (change.kind === 'Changed' &&
           validSha(change.previousFingerprint) &&
           validSha(change.currentFingerprint) &&
           change.previousFingerprint !== change.currentFingerprint) ||
          (change.kind === 'Restored' &&
           validSha(change.previousFingerprint) &&
           validSha(change.currentFingerprint)) ||
          (change.kind === 'Missing' &&
           validSha(change.previousFingerprint) &&
           change.currentFingerprint === null)
        ))
      throw new Error('SourceSyncCheckpointUnsafeResponse');
    previous = change.sourceId;
  }
}

const short = (sha: string | null) =>
  sha ? sha.slice(0, 12) + '…' : '—';

export default function SourceCheckpointRefChangePreview({
  receipt, token, preparationId, planId
}: Props) {
  const { t } = useTranslation();
  const [opened, setOpened] = useState(false);
  const [loaded, setLoaded] = useState(false);
  const [busy, setBusy] = useState(false);
  const [items, setItems] = useState<SourceCheckpointRefChange[]>([]);
  const [nextAfter, setNextAfter] = useState<string | null>(null);
  const [error, setError] = useState('');
  const generation = useRef(0);

  useEffect(() => () => { generation.current++; }, []);

  const load = async (after: string | null = null) => {
    const current = ++generation.current;
    setBusy(true);
    setError('');
    try {
      const page = await api.listSourceCheckpointRefChanges(
        token, preparationId, planId, receipt.id, after, 50);
      if (current !== generation.current) return;
      verifyPage(page, receipt, after);
      setItems(old => after ? [...old, ...page.items] : page.items);
      setNextAfter(page.nextAfter);
      setLoaded(true);
    } catch (cause) {
      if (current === generation.current)
        setError(t('cpRefDetailsFailed') + ': ' +
          (cause instanceof Error ? cause.message : 'UnknownError'));
    } finally {
      if (current === generation.current) setBusy(false);
    }
  };

  const toggle = () => {
    if (opened) { setOpened(false); generation.current++; setBusy(false); }
    else {
      setOpened(true);
      if (!loaded) void load();
    }
  };

  return <section aria-label={t('cpRefDetails')}>
    <button type="button" className="icon-button"
      onClick={toggle} aria-expanded={opened}>
      {opened ? t('cpRefDetailsClose') : t('cpRefDetails')}
    </button>
    {opened && <>
      {busy && <p className="muted" role="status">{t('cpRefDetailsLoading')}</p>}
      {error && <p className="alert" role="alert">{error}</p>}
      {loaded && items.length === 0 && !busy &&
        <p className="muted">{t('cpRefDetailsEmpty')}</p>}
      <div className="source-cp-repositories">
        {items.map(item => <div key={item.sourceId}>
          <span><strong>{item.refName}</strong>
            <small>{item.targetRepositoryName ?? t('cpRefUnmapped')}</small>
            <small>{item.sourceRepositoryId}</small></span>
          <strong>{item.kind}</strong>
          <small title={item.previousFingerprint ?? ''}>
            {short(item.previousFingerprint)} → </small>
          <small title={item.currentFingerprint ?? ''}>
            {short(item.currentFingerprint)}</small>
        </div>)}
      </div>
      {nextAfter && <button type="button" className="icon-button"
        disabled={busy} onClick={() => void load(nextAfter)}>
        {t('cpRefDetailsLoadMore')}
      </button>}
    </>}
  </section>;
}
