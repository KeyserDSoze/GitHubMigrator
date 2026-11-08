import { useCallback, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { CheckCheck, Clock3, GitCommitHorizontal, LockKeyhole, RefreshCw, ShieldCheck } from 'lucide-react';
import {
  api, type SourceSyncCheckpointPage,
  type SourceSyncCheckpointReceipt
} from './api';

type Props = {
  token: string; preparationId: string; planId: string;
  manifestSha256: string; isSuperAdmin: boolean;
};
const errorMessage = (value: unknown) =>
  value instanceof Error ? value.message : 'UnknownError';
const short = (value: string) => value.slice(0, 16) + '…';
const isSha = (value: unknown): value is string =>
  typeof value === 'string' && /^[0-9a-f]{64}$/.test(value);
function safeRefEvidence(receipt: SourceSyncCheckpointReceipt): boolean {
  if (receipt.evidenceVersion === 1) {
    return receipt.refChangeManifest == null &&
      receipt.refChangeAttestationSha256 == null;
  }
  const refs = receipt.refChangeManifest;
  return receipt.evidenceVersion === 2 &&
    refs != null &&
    isSha(refs.evidenceSha256) &&
    isSha(receipt.refChangeAttestationSha256) &&
    refs.sourceConnectionId === receipt.proof.sourceConnectionId &&
    refs.snapshotId === receipt.proof.snapshotId &&
    [refs.added, refs.changed, refs.restored, refs.missing]
      .every(x => Number.isSafeInteger(x) && x >= 0) &&
    refs.added + refs.changed + refs.restored + refs.missing ===
      receipt.proof.totalRefChanges;
}

function Checkpoint({ receipt }: { receipt: SourceSyncCheckpointReceipt }) {
  const { t } = useTranslation();
  return <article className="source-cp-entry">
    <div className="source-cp-head">
      <div><GitCommitHorizontal size={17} />
        <strong>{receipt.proof.isBaselineSnapshot
          ? t('cpBaseline') : t('cpIncremental')}</strong>
        <span>{new Date(receipt.recordedAtUtc).toLocaleString()}</span></div>
      <span><ShieldCheck size={15} /> {t('cpCommitted')}</span>
    </div>
    <div className="source-cp-evidence">
      <div><small>{t('cpSnapshot')}</small>
        <code title={receipt.proof.snapshotId}>
          {short(receipt.proof.snapshotId)}</code></div>
      <div><small>{t('cpEvidence')}</small>
        <code title={receipt.evidenceSha256}>
          {short(receipt.evidenceSha256)}</code></div>
      <div><small>{t('cpPreviousEvidence')}</small>
        {receipt.proof.previousEvidenceSha256
          ? <code title={receipt.proof.previousEvidenceSha256}>
              {short(receipt.proof.previousEvidenceSha256)}</code>
          : <span>—</span>}</div>
      <div><small>{t('cpUnmapped')}</small>
        <strong>{receipt.proof.unmappedRefChanges}</strong></div>
      {receipt.evidenceVersion === 2 && receipt.refChangeManifest &&
        <><div><small>{t('cpRefManifest')}</small>
          <code title={receipt.refChangeManifest.evidenceSha256}>
            {short(receipt.refChangeManifest.evidenceSha256)}</code></div>
        <div><small>{t('cpRefAttestation')}</small>
          <code title={receipt.refChangeAttestationSha256 ?? ''}>
            {short(receipt.refChangeAttestationSha256 ?? '')}</code></div></>}
      {receipt.evidenceVersion === 1 &&
        <div><small>{t('cpLegacy')}</small><span>—</span></div>}
    </div>
    {!receipt.proof.sourceInventoryComplete &&
      <p className="source-cp-partial">{t('cpPartial')}</p>}
    <div className="source-cp-repositories">
      {receipt.proof.repositories.map(repo =>
        <div key={repo.sourceRepositoryId}>
          <span>{repo.targetRepositoryName}
            <small>{repo.sourceRepositoryId}</small></span>
          <strong>{repo.total} {t('cpRefs')}</strong>
          <small>+{repo.added} · Δ{repo.changed} · ↻{repo.restored} · −{repo.missing}</small>
        </div>)}
    </div>
  </article>;
}

export default function SourceSyncCheckpointPanel({
  token, preparationId, planId, manifestSha256, isSuperAdmin
}: Props) {
  const { t } = useTranslation();
  const [page, setPage] = useState<SourceSyncCheckpointPage | null>(null);
  const [items, setItems] = useState<SourceSyncCheckpointReceipt[]>([]);
  const [loading, setLoading] = useState(false);
  const [capturing, setCapturing] = useState(false);
  const [error, setError] = useState('');
  const [unavailable, setUnavailable] = useState(false);
  const [notice, setNotice] = useState('');
  const sequence = useRef(0);

  const load = useCallback(async (after: string | null = null) => {
    const stamp = ++sequence.current;
    setLoading(true); setError(''); setUnavailable(false);
    try {
      const data = await api.listSourceSyncCheckpoints(
        token, preparationId, planId, after);
      if (stamp !== sequence.current) return;
      if (data.canPush || data.canRetry || data.canDispatch ||
          data.migrationComplete || data.items.some(x =>
            x.canPush || x.canRetry || x.canDispatch ||
            x.migrationComplete || !safeRefEvidence(x) ||
            x.proof.manifestSha256 !== manifestSha256 ||
            x.proof.planId !== planId || x.proof.preparationId !== preparationId))
        throw new Error('SourceSyncCheckpointUnsafeResponse');
      setPage(data);
      setItems(old => after ? [...old, ...data.items] : data.items);
    } catch (cause) {
      if (stamp === sequence.current) {
        if (errorMessage(cause).includes('SourceSyncCheckpointDisabled')) {
          setPage(null); setItems([]); setUnavailable(true);
        } else {
          setError(t('cpLoadFailed') + ': ' + errorMessage(cause));
        }
      }
    } finally {
      if (stamp === sequence.current) setLoading(false);
    }
  }, [token, preparationId, planId, manifestSha256, t]);

  useEffect(() => {
    setItems([]); setPage(null); setNotice('');
    if (isSuperAdmin) void load();
    return () => { ++sequence.current; };
  }, [isSuperAdmin, load]);

  const capture = async () => {
    if (!page?.nextEligibleSnapshotId || loading || capturing ||
        !isSuperAdmin || !window.confirm(t('cpCaptureConfirm')))
      return;
    setCapturing(true); setError(''); setNotice('');
    try {
      const receipt = await api.captureSourceSyncCheckpoint(
        token, preparationId, planId,
        page.nextEligibleSnapshotId, manifestSha256,
        page.latestCheckpointId);
      if (receipt.canPush || receipt.canRetry ||
          receipt.canDispatch || receipt.migrationComplete ||
          !safeRefEvidence(receipt) ||
          receipt.proof.snapshotId !== page.nextEligibleSnapshotId ||
          receipt.proof.manifestSha256 !== manifestSha256)
        throw new Error('SourceSyncCheckpointUnsafeResponse');
      setNotice(t('cpCaptured'));
      await load();
    } catch (cause) {
      setError(t('cpCaptureFailed') + ': ' + errorMessage(cause));
    } finally {
      setCapturing(false);
    }
  };

  return <section className="source-cp-panel" aria-label={t('cpTitle')}>
    <header className="source-cp-title">
      <div><h2><GitCommitHorizontal size={21} /> {t('cpTitle')}</h2>
        <p>{t('cpIntro')}</p></div>
      <button type="button" className="icon-button"
        disabled={loading || capturing}
        onClick={() => void load()}>
        <RefreshCw size={16} /> {t('cpRefresh')}
      </button>
    </header>

    {page && <div className="source-cp-next">
      <div><Clock3 size={19} /><div>
        <strong>{page.nextEligibleSnapshotId
          ? t('cpNextAvailable') : t('cpUpToDate')}</strong>
        <small>{page.nextEligibleSnapshotId
          ? short(page.nextEligibleSnapshotId)
          : t('cpUpToDateHint')}</small>
      </div></div>
      {page.nextEligibleSnapshotId &&
        <button type="button" className="primary"
          disabled={loading || capturing}
          onClick={() => void capture()}>
          <CheckCheck size={17} />
          {capturing ? t('cpWorking') : t('cpRecordNext')}
        </button>}
    </div>}
    <p className="source-cp-warning"><LockKeyhole size={16} /> {t('cpNoPush')}</p>
    {unavailable &&
      <p className="muted" role="status">{t('cpDisabled')}</p>}
    {error && <div className="alert" role="alert">{error}</div>}
    {notice && <div className="notice" role="status">{notice}</div>}
    {loading && <p className="muted" role="status">{t('cpLoading')}</p>}
    {page && items.length === 0 && !loading &&
      <p className="muted">{t('cpEmpty')}</p>}
    <div className="source-cp-history">
      {items.map(x => <Checkpoint key={x.id} receipt={x} />)}
    </div>
    {page?.nextAfter && <button className="icon-button source-cp-more"
      type="button" disabled={loading || capturing}
      onClick={() => void load(page.nextAfter)}>
      {t('cpLoadMore')}
    </button>}
  </section>;
}
