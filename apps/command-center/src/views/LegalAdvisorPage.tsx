import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { Button, Card, KpiCard, StatusChip } from '@practicex/design-system';
import { Gavel, Sparkles } from 'lucide-react';
import {
  legalAdvisorApi,
  readableCandidateType,
  readableRelativeTime,
  type CounselBrief,
  type LegalAdvisorPortfolio,
  type LegalAdvisorPortfolioRow,
} from '../lib/api';
import { logEvent } from '../lib/analytics';
import { LegalDisclaimer } from '../components/LegalDisclaimer';
import { MaintenancePage } from '../shell/MaintenanceMessage';

type LoadState =
  | { kind: 'loading' }
  | { kind: 'error' }
  | { kind: 'ready'; data: LegalAdvisorPortfolio };

export function LegalAdvisorPage() {
  const [state, setState] = useState<LoadState>({ kind: 'loading' });
  const [reloadKey, setReloadKey] = useState(0);
  const [batching, setBatching] = useState(false);
  const [brief, setBrief] = useState<CounselBrief | null>(null);
  const [briefLoading, setBriefLoading] = useState(false);
  const [briefError, setBriefError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const data = await legalAdvisorApi.getPortfolio();
        if (!cancelled) setState({ kind: 'ready', data });
      } catch {
        if (!cancelled) setState({ kind: 'error' });
      }
      try {
        const b = await legalAdvisorApi.getCounselBrief();
        if (!cancelled) setBrief(b);
      } catch {
        // 404 = brief not yet generated; harmless.
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [reloadKey]);

  if (state.kind === 'loading') {
    return (
      <div className="page">
        <div className="page-subtitle">Loading Counsel's portfolio…</div>
      </div>
    );
  }
  if (state.kind === 'error') {
    return <MaintenancePage eyebrow="Legal Advisor" onRetry={() => setReloadKey((k) => k + 1)} />;
  }

  const { data } = state;
  const handleBatch = async () => {
    setBatching(true);
    logEvent('legal_advisor_batch_clicked', {});
    try {
      await legalAdvisorApi.batchGenerate(false);
      setReloadKey((k) => k + 1);
    } finally {
      setBatching(false);
    }
  };

  const handleGenerateBrief = async () => {
    setBriefLoading(true);
    setBriefError(null);
    logEvent('counsel_brief_generate_clicked', {});
    try {
      const b = await legalAdvisorApi.generateCounselBrief();
      setBrief(b);
    } catch (err) {
      const detail = (err as { detail?: string }).detail;
      setBriefError(detail ?? 'Counsel brief generation failed');
    } finally {
      setBriefLoading(false);
    }
  };

  return (
    <div className="page">
      <div className="crumb">
        <span>PracticeX</span>
        <span>›</span>
        <span>Legal Advisor</span>
      </div>
      <header className="page-head" style={{ alignItems: 'flex-start' }}>
        <div style={{ flex: 1, minWidth: 0 }}>
          <div className="eyebrow">
            <span className="eyebrow-dot" />
            Premium · Counsel's posture
          </div>
          <h1 className="page-title" style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
            <Gavel size={22} style={{ color: 'var(--px-orange, #d4631e)' }} />
            Legal Advisor
          </h1>
          <div className="page-subtitle">
            A General-Counsel pass over every contract: where the landmines
            are, what to redline, what rises to material disclosure, and a
            sortable risk score. Distinct from the Document Intelligence
            Brief — that describes; this recommends.
          </div>
        </div>
        <div style={{ display: 'flex', gap: 8 }}>
          <Button onClick={handleBatch} disabled={batching}>
            {batching ? 'Generating…' : 'Generate / refresh memos'}
          </Button>
        </div>
      </header>

      <LegalDisclaimer />

      <section
        style={{
          display: 'grid',
          gridTemplateColumns: 'repeat(auto-fit, minmax(160px, 1fr))',
          gap: 12,
          marginBottom: 20,
        }}
      >
        <KpiCard label="Documents" value={String(data.counts.total)} helper={`${data.counts.withMemo} reviewed`} />
        <KpiCard
          label="Severe (81–100)"
          value={String(data.counts.severe)}
          helper="Do-not-sign or board-level"
        />
        <KpiCard
          label="High (61–80)"
          value={String(data.counts.high)}
          helper="Material exposure; redline"
        />
        <KpiCard
          label="Elevated (41–60)"
          value={String(data.counts.elevated)}
          helper="Asymmetries to monitor"
        />
        <KpiCard
          label="Modest (21–40)"
          value={String(data.counts.modest)}
          helper="Minor non-standard"
        />
        <KpiCard label="Low (0–20)" value={String(data.counts.low)} helper="Clean / market-standard" />
      </section>

      <Card
        eyebrow="Counsel's Brief"
        title="Cross-document synthesis (board-grade)"
        actions={
          <Button onClick={handleGenerateBrief} disabled={briefLoading}>
            {briefLoading ? 'Composing…' : brief ? 'Regenerate' : 'Generate'}
          </Button>
        }
      >
        {briefError ? (
          <div className="muted" style={{ color: 'var(--px-red, #b00020)' }}>
            {briefError}
          </div>
        ) : brief ? (
          <>
            <div className="muted" style={{ fontSize: 11, marginBottom: 8 }}>
              {brief.sourceDocCount} memos · {brief.model?.split('/').pop() ?? 'AI'} ·{' '}
              {readableRelativeTime(brief.generatedAt)}
            </div>
            <article className="brief-prose">
              <ReactMarkdown remarkPlugins={[remarkGfm]}>{brief.briefMd}</ReactMarkdown>
            </article>
          </>
        ) : (
          <div className="muted" style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <Sparkles size={14} />
            No Counsel's Brief yet. Generate per-document memos first, then
            click <strong>Generate</strong> above to synthesize the
            portfolio-level brief.
          </div>
        )}
      </Card>

      <Card
        eyebrow="Issue register"
        title={`Documents ranked by risk score (${data.counts.withMemo} of ${data.counts.total} reviewed)`}
        style={{ marginTop: 20 }}
      >
        {data.rows.length === 0 ? (
          <div className="muted">No documents in this tenant.</div>
        ) : (
          <div className="table-scroll">
            <table className="px-table" style={{ width: '100%', fontSize: 13 }}>
              <thead>
                <tr>
                  <th style={{ textAlign: 'left' }}>Risk</th>
                  <th style={{ textAlign: 'left' }}>Document</th>
                  <th style={{ textAlign: 'left' }}>Family</th>
                  <th style={{ textAlign: 'left' }}>Top issue</th>
                  <th style={{ textAlign: 'left' }}>Signed</th>
                  <th style={{ textAlign: 'left' }}>Generated</th>
                </tr>
              </thead>
              <tbody>
                {data.rows.map((r) => (
                  <MemoRow key={r.documentAssetId} row={r} />
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>
    </div>
  );
}

function MemoRow({ row }: { row: LegalAdvisorPortfolioRow }) {
  const score = row.riskScore;
  return (
    <tr>
      <td style={{ verticalAlign: 'top', padding: '10px 8px', minWidth: 80 }}>
        {score == null ? (
          <span className="muted" style={{ fontSize: 11 }}>—</span>
        ) : (
          <RiskBadge score={Number(score)} rating={row.riskRating} />
        )}
      </td>
      <td style={{ verticalAlign: 'top', padding: '10px 8px', maxWidth: 360 }}>
        <Link
          to={`/portfolio/${row.documentAssetId}?tab=memo`}
          style={{ fontWeight: 600, color: 'var(--px-ink, #1a1a1a)' }}
        >
          {row.fileName}
        </Link>
        {row.headline ? (
          <div className="muted" style={{ fontSize: 11, marginTop: 2, lineHeight: 1.4 }}>
            {row.headline}
          </div>
        ) : null}
      </td>
      <td style={{ verticalAlign: 'top', padding: '10px 8px' }}>
        <span className="mono-label">{readableCandidateType(row.candidateType)}</span>
      </td>
      <td style={{ verticalAlign: 'top', padding: '10px 8px', fontSize: 12 }}>
        {row.topIssueTitle ?? <span className="muted">—</span>}
      </td>
      <td style={{ verticalAlign: 'top', padding: '10px 8px' }}>
        {row.isExecuted ? (
          <StatusChip tone="ok">signed</StatusChip>
        ) : row.isExecuted === false ? (
          <StatusChip tone="warn">unsigned</StatusChip>
        ) : (
          <span className="muted" style={{ fontSize: 11 }}>—</span>
        )}
      </td>
      <td style={{ verticalAlign: 'top', padding: '10px 8px', fontSize: 11 }} className="muted">
        {row.memoExtractedAt ? readableRelativeTime(row.memoExtractedAt) : 'not generated'}
      </td>
    </tr>
  );
}

function RiskBadge({ score, rating }: { score: number; rating: string | null }) {
  const tone = riskTone(score);
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
      <span
        style={{
          display: 'inline-flex',
          alignItems: 'center',
          justifyContent: 'center',
          minWidth: 36,
          height: 28,
          padding: '0 8px',
          fontWeight: 700,
          fontSize: 13,
          borderRadius: 6,
          background: tone.bg,
          color: tone.fg,
          border: `1px solid ${tone.border}`,
        }}
      >
        {Math.round(score)}
      </span>
      {rating ? (
        <span className="mono-label" style={{ fontSize: 10, color: tone.fg }}>
          {rating.toUpperCase()}
        </span>
      ) : null}
    </div>
  );
}

function riskTone(score: number) {
  if (score >= 81) return { bg: '#fde2e2', fg: '#7d0000', border: '#cc3333' }; // severe
  if (score >= 61) return { bg: '#ffe7d3', fg: '#7a3a00', border: '#d4631e' }; // high
  if (score >= 41) return { bg: '#fff4d1', fg: '#5e4500', border: '#c8a02b' }; // elevated
  if (score >= 21) return { bg: '#e6f1ff', fg: '#1a3d6b', border: '#3a7bd5' }; // modest
  return { bg: '#dff5e7', fg: '#1d6f42', border: '#1d6f42' }; // low
}
