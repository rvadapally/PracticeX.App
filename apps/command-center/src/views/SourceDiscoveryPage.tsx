import { Button, Card, ConfidenceBar, KpiCard, StatusChip } from '@practicex/design-system';
import {
  CheckCircle2,
  FolderUp,
  Inbox,
  Loader2,
  Mail,
  PlugZap,
  RefreshCcw,
  ShieldCheck,
  Upload,
} from 'lucide-react';
import { useCallback, useEffect, useMemo, useState } from 'react';
import type {
  DocumentCandidate,
  IngestionBatch,
  IngestionBatchSummary,
  IngestionItem,
  SourceConnection,
  ConnectorDescriptor,
} from '../lib/api';
import { readableCandidateType, readableReason, sourcesApi } from '../lib/api';
import { FolderInventoryWorkflow } from './source-discovery/FolderInventoryWorkflow';

type SourceState = {
  connectors: ConnectorDescriptor[];
  connections: SourceConnection[];
  batches: IngestionBatch[];
  candidates: DocumentCandidate[];
  loading: boolean;
  error: string | null;
};

const initialState: SourceState = {
  connectors: [],
  connections: [],
  batches: [],
  candidates: [],
  loading: true,
  error: null,
};

export function SourceDiscoveryPage() {
  const [state, setState] = useState<SourceState>(initialState);
  const [uploadOpen, setUploadOpen] = useState(false);
  const [activeBatch, setActiveBatch] = useState<IngestionBatchSummary | null>(null);
  const [actionInflight, setActionInflight] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    setState((s) => ({ ...s, loading: true, error: null }));
    try {
      const [connectors, connections, batches, candidates] = await Promise.all([
        sourcesApi.listConnectors(),
        sourcesApi.listConnections(),
        sourcesApi.listBatches(10),
        sourcesApi.listCandidates({ limit: 25 }),
      ]);
      setState({ connectors, connections, batches, candidates, loading: false, error: null });
    } catch (err) {
      const message = err instanceof Error ? err.message : (err as { detail?: string }).detail ?? 'Unable to reach API';
      setState((s) => ({ ...s, loading: false, error: message }));
    }
  }, []);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  const folderConnection = useMemo(
    () => state.connections.find((c) => c.sourceType === 'local_folder' && c.status !== 'disabled'),
    [state.connections],
  );
  const outlookConnection = useMemo(
    () => state.connections.find((c) => c.sourceType === 'outlook_mailbox' && c.status !== 'disabled'),
    [state.connections],
  );

  const folderConnector = state.connectors.find((c) => c.sourceType === 'local_folder');
  const outlookConnector = state.connectors.find((c) => c.sourceType === 'outlook_mailbox');

  const ensureConnection = useCallback(
    async (sourceType: string, displayName: string): Promise<SourceConnection> => {
      const existing = state.connections.find(
        (c) => c.sourceType === sourceType && c.status !== 'disabled',
      );
      if (existing) {
        return existing;
      }
      const created = await sourcesApi.createConnection(sourceType, displayName);
      setState((s) => ({ ...s, connections: [created, ...s.connections] }));
      return created;
    },
    [state.connections],
  );

  const [folderConnId, setFolderConnId] = useState<string | null>(null);
  const [notice, setNotice] = useState<{ tone: 'warn' | 'error'; text: string } | null>(null);

  const openFolderWorkflow = useCallback(async () => {
    setNotice(null);
    try {
      const conn = await ensureConnection('local_folder', 'Local folder upload');
      setFolderConnId(conn.id);
      setUploadOpen(true);
    } catch (err) {
      const detail = (err as { detail?: string; title?: string }).detail
        ?? (err as { title?: string }).title
        ?? 'Could not initialise the folder connection. Check the API is running on https://localhost:7100.';
      setNotice({ tone: 'error', text: detail });
    }
  }, [ensureConnection]);

  const outlookNotConfigured = outlookConnector?.status === 'configuration_required';

  const handleConnectOutlook = useCallback(async () => {
    setNotice(null);
    if (outlookNotConfigured) {
      setNotice({
        tone: 'warn',
        text: 'Outlook is not configured yet. Set MicrosoftGraph__ClientId, MicrosoftGraph__ClientSecret, and MicrosoftGraph__TenantId on the API host (see docs/source-discovery.md).',
      });
      return;
    }
    setActionInflight('outlook_connect');
    try {
      const conn = await ensureConnection('outlook_mailbox', 'Outlook mailbox');
      const startResponse = await sourcesApi.startOutlookOAuth(conn.id);
      window.location.href = startResponse.authorizeUrl;
    } catch (err) {
      const detail = (err as { detail?: string }).detail
        ?? 'Microsoft Graph is not configured yet. Set MicrosoftGraph__ClientId and MicrosoftGraph__ClientSecret on the API host.';
      setNotice({ tone: 'warn', text: detail });
    } finally {
      setActionInflight(null);
    }
  }, [ensureConnection, outlookNotConfigured]);

  const handleScanOutlook = useCallback(async () => {
    if (!outlookConnection || outlookConnection.status !== 'connected') {
      return;
    }
    setNotice(null);
    setActionInflight('outlook_scan');
    try {
      const summary = await sourcesApi.scanOutlook(outlookConnection.id, 25);
      setActiveBatch(summary);
      await refresh();
    } catch (err) {
      const detail = (err as { detail?: string }).detail ?? 'Scan failed.';
      setNotice({ tone: 'error', text: detail });
    } finally {
      setActionInflight(null);
    }
  }, [outlookConnection, refresh]);

  const handleQueueReview = useCallback(
    async (candidateId: string) => {
      setActionInflight(`queue_${candidateId}`);
      try {
        await sourcesApi.queueReview(candidateId);
        await refresh();
      } finally {
        setActionInflight(null);
      }
    },
    [refresh],
  );

  const handleRetry = useCallback(
    async (candidateId: string) => {
      setActionInflight(`retry_${candidateId}`);
      try {
        await sourcesApi.retryCandidate(candidateId);
        await refresh();
      } finally {
        setActionInflight(null);
      }
    },
    [refresh],
  );

  const totals = useMemo(() => {
    return state.batches.reduce(
      (acc, b) => ({
        files: acc.files + b.fileCount,
        candidates: acc.candidates + b.candidateCount,
        skipped: acc.skipped + b.skippedCount,
      }),
      { files: 0, candidates: 0, skipped: 0 },
    );
  }, [state.batches]);

  const pendingReview = state.candidates.filter((c) => c.status === 'pending_review').length;
  const skippedNoReview = state.candidates.filter((c) => c.status === 'skipped').length;

  return (
    <div className="page">
      <div className="crumb">
        <span>Command center</span>
        <span>›</span>
        <span>Source discovery</span>
      </div>
      <header className="page-head">
        <div>
          <div className="eyebrow">
            <span className="eyebrow-dot" />
            Connectors · governed candidate discovery
          </div>
          <h1 className="page-title">Discover contract evidence</h1>
          <div className="page-subtitle">
            Scan local folders and Outlook attachments. Every discovered document creates a source object,
            ingestion job, and document candidate — nothing becomes a canonical contract until a reviewer says so.
          </div>
        </div>
        <div style={{ display: 'flex', gap: 10 }}>
          <Button variant="secondary" onClick={() => void refresh()} disabled={state.loading}>
            <RefreshCcw size={14} /> Refresh
          </Button>
          <Button onClick={() => void openFolderWorkflow()}>
            <Upload size={14} /> Upload folder
          </Button>
        </div>
      </header>

      {state.error ? (
        <div
          style={{
            background: 'var(--px-orange-soft)',
            border: '1px solid var(--px-orange)',
            borderRadius: 'var(--px-radius)',
            color: 'var(--px-orange)',
            marginBottom: 18,
            padding: '10px 14px',
            fontSize: 13,
          }}
        >
          API unreachable — {state.error}. The UI will still render but actions will fail until the API is up.
        </div>
      ) : null}

      {notice ? (
        <div
          role="status"
          style={{
            alignItems: 'center',
            background: notice.tone === 'error' ? 'var(--px-orange-soft)' : 'var(--px-surface-2)',
            border: `1px solid ${notice.tone === 'error' ? 'var(--px-orange)' : 'var(--px-line)'}`,
            borderRadius: 'var(--px-radius)',
            color: notice.tone === 'error' ? 'var(--px-orange)' : 'var(--px-text)',
            display: 'flex',
            fontSize: 13,
            gap: 12,
            justifyContent: 'space-between',
            marginBottom: 18,
            padding: '10px 14px',
          }}
        >
          <span>{notice.text}</span>
          <button
            type="button"
            className="px-button ghost"
            onClick={() => setNotice(null)}
            aria-label="Dismiss"
            style={{ fontSize: 12 }}
          >
            Dismiss
          </button>
        </div>
      ) : null}

      <section className="kpi-grid">
        <KpiCard label="Files discovered" value={totals.files} helper="Across recent batches" />
        <KpiCard label="Candidates" value={totals.candidates} helper="Open for selection" tone="accent" />
        <KpiCard label="Pending review" value={pendingReview} helper="Awaiting human QA" tone="warn" />
        <KpiCard label="Skipped" value={skippedNoReview + totals.skipped} helper="Duplicates or unsupported" />
      </section>

      <section className="grid-2">
        <Card title="Available connectors" eyebrow={`${state.connectors.length} registered`}>
          <div className="source-card">
            <ConnectorSourceCard
              icon={<FolderUp size={17} />}
              title="Folder upload"
              body={
                folderConnector?.summary ??
                'Drag a folder or files. Relative paths are kept as folder hints, contents are hashed for dedupe.'
              }
              connection={folderConnection ?? null}
              status="ready"
              actions={
                <Button onClick={() => void openFolderWorkflow()}>
                  <FolderUp size={14} /> Upload folder
                </Button>
              }
            />
            <ConnectorSourceCard
              icon={<Mail size={17} />}
              title="Outlook mailbox"
              body={
                outlookConnector?.summary ??
                'Read-only Microsoft Graph search across the connected mailbox for contract-like attachments.'
              }
              connection={outlookConnection ?? null}
              status={outlookConnector?.status ?? 'unknown'}
              actions={
                outlookConnection?.status === 'connected' ? (
                  <Button onClick={handleScanOutlook} disabled={actionInflight === 'outlook_scan'}>
                    {actionInflight === 'outlook_scan' ? (
                      <Loader2 size={14} className="spin" />
                    ) : (
                      <Inbox size={14} />
                    )}{' '}
                    Scan inbox
                  </Button>
                ) : (
                  <Button
                    onClick={handleConnectOutlook}
                    disabled={actionInflight === 'outlook_connect' || outlookNotConfigured}
                    title={outlookNotConfigured ? 'Microsoft Graph credentials not configured on the API host' : undefined}
                  >
                    <PlugZap size={14} />
                    {outlookNotConfigured ? 'Configuration required' : 'Connect Outlook'}
                  </Button>
                )
              }
            />
            <ConnectorSourceCard
              icon={<ShieldCheck size={17} />}
              title="Governance guardrails"
              body="Connectors are read-only. Candidates require explicit reviewer action before becoming a contract record. Every action is audit-logged."
              status="enterprise_rule"
            />
          </div>
        </Card>

        <Card
          title="Recent batches"
          eyebrow={`${state.batches.length} runs`}
          actions={
            state.batches.length > 0 ? (
              <Button variant="ghost" onClick={() => void refresh()}>
                <RefreshCcw size={13} />
              </Button>
            ) : null
          }
        >
          {state.batches.length === 0 ? (
            <div className="muted" style={{ paddingTop: 6 }}>
              No ingestion batches yet. Upload a folder or scan an inbox to populate.
            </div>
          ) : (
            state.batches.map((batch) => (
              <div className="source-row" key={batch.id} style={{ gridTemplateColumns: '46px minmax(0, 1fr) 240px' }}>
                <div className="source-icon">
                  {batch.sourceType === 'outlook_mailbox' ? <Mail size={16} /> : <FolderUp size={16} />}
                </div>
                <div>
                  <strong>{batch.sourceType.replace(/_/g, ' ')}</strong>
                  <div className="muted">
                    {new Date(batch.createdAt).toLocaleString()} · {batch.fileCount} files
                  </div>
                </div>
                <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end' }}>
                  <StatusChip tone={batch.candidateCount > 0 ? 'ok' : 'muted'}>
                    {batch.candidateCount} candidates
                  </StatusChip>
                  {batch.skippedCount > 0 ? (
                    <StatusChip tone="warn">{batch.skippedCount} skipped</StatusChip>
                  ) : null}
                  {batch.errorCount > 0 ? (
                    <StatusChip tone="accent">{batch.errorCount} errors</StatusChip>
                  ) : null}
                </div>
              </div>
            ))
          )}
        </Card>
      </section>

      {activeBatch ? (
        <section style={{ marginTop: 22 }}>
          <Card
            title="Latest scan results"
            eyebrow={`Batch ${activeBatch.batchId.slice(0, 8)} · ${activeBatch.status}`}
            actions={
              <Button variant="secondary" onClick={() => setActiveBatch(null)}>
                Dismiss
              </Button>
            }
          >
            <BatchResultsTable items={activeBatch.items} />
          </Card>
        </section>
      ) : null}

      <section style={{ marginTop: 22 }}>
        <Card
          title="Document candidates"
          eyebrow={`${state.candidates.length} surfaced`}
          actions={
            <a href="/review" className="px-button secondary">
              <CheckCircle2 size={14} /> Open review queue
            </a>
          }
        >
          <CandidatesTable
            candidates={state.candidates}
            onQueueReview={handleQueueReview}
            onRetry={handleRetry}
            actionInflight={actionInflight}
          />
        </Card>
      </section>

      {uploadOpen && folderConnId ? (
        <FolderInventoryWorkflow
          connectionId={folderConnId}
          onClose={() => setUploadOpen(false)}
          onComplete={async (summary) => {
            setActiveBatch(summary);
            await refresh();
          }}
        />
      ) : null}
    </div>
  );
}

function ConnectorSourceCard({
  icon,
  title,
  body,
  status,
  connection,
  actions,
}: {
  icon: React.ReactNode;
  title: string;
  body: string;
  status: string;
  connection?: SourceConnection | null;
  actions?: React.ReactNode;
}) {
  const tone = connection?.status === 'connected' ? 'ok' : connection?.status === 'error' ? 'accent' : 'muted';
  const statusLabel = connection?.status
    ? connection.status.replace(/_/g, ' ')
    : status.replace(/_/g, ' ');
  return (
    <div className="source-row">
      <div className="source-icon">{icon}</div>
      <div>
        <strong>{title}</strong>
        <div className="muted">{body}</div>
        {connection?.lastError ? (
          <div className="mono-label" style={{ color: 'var(--px-orange)', marginTop: 6 }}>
            {connection.lastError}
          </div>
        ) : null}
        {connection?.oauthSubject ? (
          <div className="mono-label" style={{ marginTop: 6 }}>
            Connected as {connection.oauthSubject}
          </div>
        ) : null}
      </div>
      <StatusChip tone={tone}>{statusLabel}</StatusChip>
      {actions ?? <span />}
    </div>
  );
}

function BatchResultsTable({ items }: { items: IngestionItem[] }) {
  if (items.length === 0) {
    return <div className="muted">No items in this batch.</div>;
  }
  return (
    <div style={{ margin: '0 -18px' }}>
      {items.map((item) => (
        <div className="field-row" key={item.sourceObjectId} style={{ gridTemplateColumns: '1fr 160px 160px 140px' }}>
          <div>
            <div className="field-value">{item.name}</div>
            <div className="field-source">
              {item.relativePath ? `path: ${item.relativePath} · ` : ''}
              {item.reasonCodes.slice(0, 3).map(readableReason).join(' · ')}
            </div>
          </div>
          <div className="mono-label">{readableCandidateType(item.candidateType)}</div>
          <div className="confidence">
            <ConfidenceBar value={Math.round(item.confidence * 100)} tone={item.confidence < 0.55 ? 'accent' : 'ok'} />
            <span className="mono-label">{Math.round(item.confidence * 100)}%</span>
          </div>
          <StatusChip tone={item.status === 'skipped' ? 'warn' : item.status === 'pending_review' ? 'accent' : 'ok'}>
            {item.status.replace(/_/g, ' ')}
          </StatusChip>
        </div>
      ))}
    </div>
  );
}

function CandidatesTable({
  candidates,
  onQueueReview,
  onRetry,
  actionInflight,
}: {
  candidates: DocumentCandidate[];
  onQueueReview: (id: string) => void;
  onRetry: (id: string) => void;
  actionInflight: string | null;
}) {
  if (candidates.length === 0) {
    return <div className="muted">No candidates yet. Run a scan or upload a folder.</div>;
  }
  return (
    <div style={{ margin: '0 -18px' }}>
      {candidates.map((c) => {
        const conf = Math.round(c.confidence * 100);
        return (
          <div
            className="field-row"
            key={c.id}
            style={{ gridTemplateColumns: '1fr 160px 150px 130px 130px' }}
          >
            <div>
              <div className="field-value">{c.originFilename ?? `Candidate ${c.id.slice(0, 8)}`}</div>
              <div className="field-source">
                {c.relativePath ? `path: ${c.relativePath} · ` : ''}
                {c.reasonCodes.slice(0, 4).map(readableReason).join(' · ')}
                {c.counterpartyHint ? ` · counterparty hint: ${c.counterpartyHint}` : ''}
              </div>
            </div>
            <div className="mono-label">{readableCandidateType(c.candidateType)}</div>
            <div className="confidence">
              <ConfidenceBar value={conf} tone={conf < 55 ? 'accent' : 'ok'} />
              <span className="mono-label">{conf}%</span>
            </div>
            <StatusChip
              tone={
                c.status === 'pending_review'
                  ? 'accent'
                  : c.status === 'skipped'
                  ? 'warn'
                  : c.status === 'rejected'
                  ? 'accent'
                  : 'ok'
              }
            >
              {c.status.replace(/_/g, ' ')}
            </StatusChip>
            <div style={{ display: 'flex', gap: 6 }}>
              {c.status === 'skipped' ? (
                <Button
                  variant="secondary"
                  onClick={() => onRetry(c.id)}
                  disabled={actionInflight === `retry_${c.id}`}
                >
                  Retry
                </Button>
              ) : (
                <Button
                  variant="secondary"
                  onClick={() => onQueueReview(c.id)}
                  disabled={actionInflight === `queue_${c.id}` || c.status === 'pending_review'}
                >
                  Queue
                </Button>
              )}
            </div>
          </div>
        );
      })}
    </div>
  );
}
