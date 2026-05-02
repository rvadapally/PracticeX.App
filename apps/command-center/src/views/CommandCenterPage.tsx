import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { Button, Card, KpiCard } from '@practicex/design-system';
import { analysisApi, type DashboardStats, type Portfolio, type RenewalsResponse } from '../lib/api';

type LoadState =
  | { kind: 'loading' }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; stats: DashboardStats; portfolio: Portfolio; renewals: RenewalsResponse | null };

export function CommandCenterPage() {
  const [state, setState] = useState<LoadState>({ kind: 'loading' });

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const [stats, portfolio, renewals] = await Promise.all([
          analysisApi.getDashboard(),
          analysisApi.getPortfolio(),
          analysisApi.getRenewals().catch(() => null),
        ]);
        if (!cancelled) setState({ kind: 'ready', stats, portfolio, renewals });
      } catch (err) {
        if (cancelled) return;
        const message =
          (err as { detail?: string })?.detail ??
          (err as { title?: string })?.title ??
          (err instanceof Error ? err.message : null) ??
          `Failed to load (HTTP ${(err as { status?: number })?.status ?? 'unknown'}).`;
        setState({ kind: 'error', message });
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  if (state.kind === 'loading') {
    return <div className="page"><div className="page-subtitle">Loading…</div></div>;
  }
  if (state.kind === 'error') {
    return <div className="page"><div className="banner banner-error">{state.message}</div></div>;
  }

  const { stats, portfolio, renewals } = state;
  const overdueCount = renewals?.counts.overdue ?? 0;
  const next30 = renewals?.counts.within30 ?? 0;
  const renewalsHelper = renewals
    ? overdueCount > 0
      ? `${overdueCount} overdue · ${next30} in next 30 days`
      : next30 > 0
      ? `${next30} action${next30 === 1 ? '' : 's'} in next 30 days`
      : `${renewals.counts.total} total upcoming actions`
    : 'Loading…';
  const renewalsTone: 'warn' | 'accent' | undefined =
    overdueCount > 0 ? 'warn' : next30 > 0 ? 'accent' : undefined;
  const today = new Date().toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });

  return (
    <div className="page">
      <div className="crumb">
        <span>PracticeX</span>
        <span>›</span>
        <span>Command center</span>
      </div>
      <header className="page-head">
        <div>
          <div className="eyebrow"><span className="eyebrow-dot" />Operations overview · {today}</div>
          <h1 className="page-title">Command center</h1>
          <div className="page-subtitle">
            What we've read from your filing cabinet so far. Click anywhere to drill in.
          </div>
        </div>
        <div style={{ display: 'flex', gap: 10 }}>
          <Link to="/portfolio"><Button variant="secondary">View portfolio</Button></Link>
          <Link to="/sources"><Button>Upload documents</Button></Link>
        </div>
      </header>

      <section className="kpi-grid">
        <Link to="/portfolio" className="kpi-link" aria-label="Open portfolio">
          <KpiCard
            label="Documents processed"
            value={stats.documents.toString()}
            helper={`${portfolio.totalPages} pages · ${stats.totalSizeMb.toFixed(1)} MB`}
          />
        </Link>
        <Link to="/portfolio" className="kpi-link" aria-label="Open candidates">
          <KpiCard
            label="Candidates extracted"
            value={stats.candidates.toString()}
            helper={`Across ${stats.ingestionBatches} ingestion batch${stats.ingestionBatches === 1 ? '' : 'es'}`}
            tone="accent"
          />
        </Link>
        <Link to="/review" className="kpi-link" aria-label="Open review queue">
          <KpiCard
            label="Awaiting review"
            value={stats.reviewQueueDepth.toString()}
            helper={stats.reviewQueueDepth === 0 ? 'Queue empty' : 'Confirm or correct extractions'}
            tone={stats.reviewQueueDepth > 0 ? 'warn' : undefined}
          />
        </Link>
        <Link to="/renewals" className="kpi-link" aria-label="Open renewals timeline">
          <KpiCard
            label="Upcoming actions"
            value={renewals ? (overdueCount + next30).toString() : '—'}
            helper={renewalsHelper}
            tone={renewalsTone}
          />
        </Link>
      </section>

      <section className="grid-2">
        <Card title="Document families">
          {portfolio.families.length === 0 ? (
            <div className="muted">No documents yet. Upload some to get started.</div>
          ) : (
            <div className="doc-table">
              {portfolio.families.map((f) => (
                <Link
                  key={f.family}
                  to={`/portfolio?family=${encodeURIComponent(f.family)}`}
                  className="doc-row"
                  style={{
                    gridTemplateColumns: 'minmax(0, 1.4fr) 80px 90px 90px',
                    textDecoration: 'none',
                  }}
                >
                  <div className="doc-row-name">{f.family.replace(/_/g, ' ')}</div>
                  <div className="doc-row-meta">{f.documentCount} doc{f.documentCount === 1 ? '' : 's'}</div>
                  <div className="doc-row-meta">{f.totalPages} pgs</div>
                  <div className="doc-row-meta">{f.totalSizeMb.toFixed(1)} MB</div>
                </Link>
              ))}
            </div>
          )}
        </Card>
        <Card title="Recent candidates">
          {portfolio.documents.length === 0 ? (
            <div className="muted">No candidates yet.</div>
          ) : (
            <div className="doc-table">
              {portfolio.documents.slice(0, 6).map((d) => (
                <Link key={d.documentAssetId} to={`/portfolio/${d.documentAssetId}`} className="doc-row" style={{ textDecoration: 'none', gridTemplateColumns: 'minmax(0, 1.4fr) 1fr 80px' }}>
                  <div className="doc-row-name" title={d.fileName}>{d.fileName}</div>
                  <div className="doc-row-type">
                    {d.candidateType.replace(/_/g, ' ')}
                    {d.extractedSubtype ? <span className="muted"> · {d.extractedSubtype}</span> : null}
                  </div>
                  <div className="doc-row-meta">{d.pageCount ?? '—'} pg</div>
                </Link>
              ))}
            </div>
          )}
        </Card>
      </section>
    </div>
  );
}
