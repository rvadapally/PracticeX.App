import { useEffect, useMemo, useRef, useState, type ReactElement } from 'react';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import { Button, Card, ConfidenceBar, StatusChip } from '@practicex/design-system';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import {
  analysisApi,
  legalAdvisorApi,
  parseLegalMemoJson,
  type DocumentDetail,
  type ExtractedField,
  type LegalMemoIssue,
  type LegalMemoResult,
  type LegalMemoStructured,
  readableCandidateType,
} from '../lib/api';
import { logEvent } from '../lib/analytics';
import { LegalDisclaimer } from '../components/LegalDisclaimer';
import { MaintenancePage } from '../shell/MaintenanceMessage';

type RightPaneTab = 'brief' | 'fields' | 'memo';

const API_BASE = (import.meta.env.VITE_API_BASE as string | undefined) ?? '/api';

type LoadState =
  | { kind: 'loading' }
  | { kind: 'error' }
  | { kind: 'ready'; detail: DocumentDetail };

export function DocumentDetailPage() {
  const { assetId } = useParams<{ assetId: string }>();
  const [searchParams] = useSearchParams();
  const initialTab = (searchParams.get('tab') as RightPaneTab) || 'brief';
  const [state, setState] = useState<LoadState>({ kind: 'loading' });
  const [reloadKey, setReloadKey] = useState(0);
  const [tab, setTab] = useState<RightPaneTab>(
    initialTab === 'memo' || initialTab === 'fields' || initialTab === 'brief' ? initialTab : 'brief',
  );
  const [activeCitation, setActiveCitation] = useState<string | null>(null);
  const [flashKey, setFlashKey] = useState(0);
  const [memo, setMemo] = useState<LegalMemoResult | null>(null);
  const [memoLoading, setMemoLoading] = useState(false);
  const [memoError, setMemoError] = useState<string | null>(null);
  const snippetRef = useRef<HTMLPreElement>(null);

  function focusCitation(citation: string) {
    setActiveCitation(citation);
    setFlashKey((k) => k + 1);
    // Defer to allow render cycle to mark the highlight target.
    setTimeout(() => {
      const target = snippetRef.current?.querySelector('[data-citation-anchor="true"]') as HTMLElement | null;
      if (target) {
        target.scrollIntoView({ behavior: 'smooth', block: 'center' });
      } else if (snippetRef.current) {
        snippetRef.current.scrollTo({ top: 0, behavior: 'smooth' });
      }
    }, 30);
  }

  useEffect(() => {
    if (!assetId) return;
    let cancelled = false;
    (async () => {
      try {
        const detail = await analysisApi.getDocument(assetId);
        if (!cancelled) {
          setState({ kind: 'ready', detail });
          logEvent('document_open', {
            assetId,
            fileName: detail.fileName ?? null,
            candidateType: detail.candidateType ?? null,
          });
        }
      } catch {
        if (cancelled) return;
        setState({ kind: 'error' });
      }
      // Best-effort memo load. 404 is the common "not generated yet" path.
      try {
        const m = await legalAdvisorApi.getMemo(assetId);
        if (!cancelled) setMemo(m);
      } catch {
        // ignore
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [assetId, reloadKey]);

  async function generateMemo() {
    if (!assetId) return;
    setMemoLoading(true);
    setMemoError(null);
    logEvent('legal_memo_generate_clicked', { assetId });
    try {
      const m = await legalAdvisorApi.generateMemo(assetId);
      setMemo(m);
    } catch (err) {
      const detail = (err as { detail?: string }).detail;
      setMemoError(detail ?? 'Counsel memo generation failed');
    } finally {
      setMemoLoading(false);
    }
  }

  const sourceUrl = useMemo(() => {
    if (!assetId) return null;
    return `${API_BASE}/analysis/documents/${assetId}/content`;
  }, [assetId]);

  if (state.kind === 'loading') {
    return <div className="page"><div className="page-subtitle">Loading document…</div></div>;
  }
  if (state.kind === 'error') {
    return (
      <MaintenancePage
        eyebrow="Document"
        onRetry={() => setReloadKey((k) => k + 1)}
      />
    );
  }

  const { detail } = state;
  const isPdf = !!detail.fileName && detail.fileName.toLowerCase().endsWith('.pdf');

  // LLM fields take priority when present; regex stays as fallback for "show old".
  const llmFields = detail.llmExtractedFields?.fields ?? [];
  const regexFields = detail.extractedFields?.fields ?? [];
  const hasLlm = llmFields.length > 0;
  const fields = hasLlm ? llmFields : regexFields;

  return (
    <div className="page document-detail-page">
      <div className="crumb">
        <Link to="/portfolio">Portfolio</Link>
        <span>›</span>
        <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', maxWidth: 600, display: 'inline-block', verticalAlign: 'bottom' }}>{detail.fileName}</span>
      </div>
      <header className="page-head" style={{ alignItems: 'flex-start' }}>
        <div style={{ minWidth: 0, flex: 1 }}>
          <div className="eyebrow">
            <span className="eyebrow-dot" />
            {readableCandidateType(detail.candidateType ?? 'unknown')}
            {detail.extractedSubtype ? <span> · {detail.extractedSubtype}</span> : null}
          </div>
          <h1 className="page-title" style={{ wordBreak: 'break-word', fontSize: 22 }}>{detail.fileName}</h1>
          <div className="page-subtitle">
            {detail.pageCount ? `${detail.pageCount} pages` : 'page count unknown'}
            {detail.layoutProvider ? <> · OCR via {detail.layoutProvider}</> : null}
            {detail.extractorName ? <> · {detail.extractorName}</> : null}
          </div>
        </div>
        <div style={{ display: 'flex', gap: 8, alignItems: 'flex-start', flexWrap: 'wrap', justifyContent: 'flex-end' }}>
          {detail.isExecuted ? <StatusChip tone="ok">signed</StatusChip> : null}
          {detail.isTemplate ? <StatusChip tone="warn">template / unsigned</StatusChip> : null}
          <StatusChip tone={detail.extractionStatus === 'completed' ? 'ok' : 'muted'}>
            {detail.extractionStatus ?? 'pending'}
          </StatusChip>
        </div>
      </header>

      <LlmActionRow detail={detail} hasLlm={hasLlm} onUpdated={(d) => setState({ kind: 'ready', detail: d })} />

      <section className="document-split">
        <Card
          eyebrow={
            detail.layoutProvider
              ? `OCR via ${detail.layoutProvider}${detail.layoutPageCount ? ` · ${detail.layoutPageCount} pages` : ''}`
              : 'Local text extraction'
          }
          title="What we read from this document"
          className="document-source-card"
          actions={
            sourceUrl ? (
              <a
                href={sourceUrl}
                target="_blank"
                rel="noreferrer"
                className="px-button"
                style={{
                  textDecoration: 'none',
                  fontSize: 12,
                  padding: '6px 14px',
                  background: 'var(--px-orange, #d4631e)',
                  color: '#fff',
                  borderRadius: 6,
                  fontWeight: 600,
                }}
              >
                {isPdf ? '📄 Open PDF' : '📄 Open original'}
              </a>
            ) : null
          }
        >
          {!sourceUrl ? (
            <div className="muted">No source URL.</div>
          ) : detail.layoutSnippet ? (
            <>
              <div className="muted" style={{ fontSize: 12, marginBottom: 10, lineHeight: 1.5 }}>
                Text extracted from{' '}
                <strong>{detail.fileName}</strong>
                {detail.pageCount ? ` (${detail.pageCount} pages)` : ''}.
                {activeCitation ? (
                  <span style={{ color: 'var(--px-orange, #d4631e)' }}>
                    {' '}Highlighted: passage tied to the canonical-headline field you clicked.
                  </span>
                ) : (
                  ' Click any canonical-headline card on the right to highlight its source passage here.'
                )}
              </div>
              <LayoutSnippetPane
                snippetRef={snippetRef}
                snippet={detail.layoutSnippet}
                citation={activeCitation}
                flashKey={flashKey}
              />
              {detail.layoutSnippet.endsWith('...') ? (
                <div className="muted" style={{ fontSize: 11, marginTop: 8, fontStyle: 'italic' }}>
                  Truncated preview - click "Open {isPdf ? 'PDF' : 'original'}" to view the full document.
                </div>
              ) : null}
            </>
          ) : (
            <div className="muted">
              Text extraction pending or unavailable. Click "Open {isPdf ? 'PDF' : 'original'}" to view the document directly.
            </div>
          )}
        </Card>

        <Card className="document-fields-card" style={{ padding: 0 }}>
          <RightPaneTabs
            tab={tab}
            onTabChange={setTab}
            hasBrief={!!detail.narrativeBriefMd}
            briefModel={detail.narrativeModel}
            briefAt={detail.narrativeExtractedAt}
            hasLlm={hasLlm}
            fieldCount={fields.length}
            hasMemo={!!memo}
            memoRiskScore={memo?.riskScore ?? null}
          />
          <div className="right-pane-body">
            {tab === 'brief' ? (
              <BriefPane detail={detail} />
            ) : tab === 'fields' ? (
              <FieldsPane
                detail={detail}
                fields={fields}
                hasLlm={hasLlm}
                onCitationClick={focusCitation}
                activeCitation={activeCitation}
              />
            ) : (
              <MemoPane
                memo={memo}
                onGenerate={generateMemo}
                loading={memoLoading}
                error={memoError}
              />
            )}
          </div>
        </Card>
      </section>
    </div>
  );
}

function RightPaneTabs({
  tab,
  onTabChange,
  hasBrief,
  briefModel,
  briefAt,
  hasLlm,
  fieldCount,
  hasMemo,
  memoRiskScore,
}: {
  tab: RightPaneTab;
  onTabChange: (t: RightPaneTab) => void;
  hasBrief: boolean;
  briefModel: string | null;
  briefAt: string | null;
  hasLlm: boolean;
  fieldCount: number;
  hasMemo: boolean;
  memoRiskScore: number | null;
}) {
  return (
    <div className="right-pane-tabs">
      <button
        className={`right-pane-tab ${tab === 'brief' ? 'is-active' : ''}`}
        onClick={() => onTabChange('brief')}
      >
        <span className="right-pane-tab-label">Intelligence Brief</span>
        {hasBrief ? (
          <span className="right-pane-tab-meta">
            {briefModel?.split('/').pop() ?? 'authored'} ·{' '}
            {briefAt ? new Date(briefAt).toLocaleDateString() : '—'}
          </span>
        ) : (
          <span className="right-pane-tab-meta muted">not yet generated</span>
        )}
      </button>
      <button
        className={`right-pane-tab ${tab === 'fields' ? 'is-active' : ''}`}
        onClick={() => onTabChange('fields')}
      >
        <span className="right-pane-tab-label">Extracted Fields</span>
        <span className="right-pane-tab-meta">
          {fieldCount > 0 ? `${fieldCount} fields` : 'none yet'}
          {hasLlm ? ' · LLM' : ''}
        </span>
      </button>
      <button
        className={`right-pane-tab ${tab === 'memo' ? 'is-active' : ''}`}
        onClick={() => onTabChange('memo')}
      >
        <span className="right-pane-tab-label">Counsel's Memo</span>
        <span className="right-pane-tab-meta">
          {hasMemo ? (
            memoRiskScore != null ? (
              <span>Risk {Math.round(Number(memoRiskScore))}/100</span>
            ) : (
              'generated'
            )
          ) : (
            <span className="muted">premium · not yet generated</span>
          )}
        </span>
      </button>
    </div>
  );
}

function MemoPane({
  memo,
  onGenerate,
  loading,
  error,
}: {
  memo: LegalMemoResult | null;
  onGenerate: () => Promise<void>;
  loading: boolean;
  error: string | null;
}) {
  const structured = useMemo<LegalMemoStructured | null>(
    () => (memo ? parseLegalMemoJson(memo.memoJson) : null),
    [memo],
  );

  if (!memo && !loading) {
    return (
      <div className="memo-pane">
        <LegalDisclaimer />
        <div className="muted brief-empty">
          <div style={{ fontSize: 14, marginBottom: 8 }}>
            No Counsel's Memo generated yet for this document.
          </div>
          <div style={{ fontSize: 13, marginBottom: 14 }}>
            The Counsel's Memo is a premium General-Counsel-grade analysis:
            issue register with severity, proposed redlines, material
            disclosures (board / insurer / lender / M&A / regulator), and
            a 0–100 risk score sortable across the portfolio.
          </div>
          <Button onClick={onGenerate}>Generate Counsel's Memo</Button>
          {error ? (
            <div style={{ marginTop: 10, color: 'var(--px-red, #b00020)', fontSize: 12 }}>
              {error}
            </div>
          ) : null}
        </div>
      </div>
    );
  }

  if (loading && !memo) {
    return (
      <div className="memo-pane">
        <div className="muted">Composing Counsel's Memo… (~10–20 sec)</div>
      </div>
    );
  }

  if (!memo) return null;

  return (
    <div className="memo-pane">
      <LegalDisclaimer />
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 12,
          marginBottom: 14,
          flexWrap: 'wrap',
        }}
      >
        {memo.riskScore != null ? (
          <RiskScorePill score={Number(memo.riskScore)} rating={structured?.risk_rating ?? null} />
        ) : null}
        <div className="muted" style={{ fontSize: 11 }}>
          {memo.model?.split('/').pop() ?? 'AI'} · {memo.tokensIn + memo.tokensOut} tokens · {memo.latencyMs}ms
        </div>
        <div style={{ marginLeft: 'auto' }}>
          <Button variant="secondary" onClick={onGenerate} disabled={loading}>
            {loading ? 'Regenerating…' : 'Regenerate'}
          </Button>
        </div>
      </div>

      {structured?.issues && structured.issues.length > 0 ? (
        <IssueRegister issues={structured.issues} />
      ) : null}

      <article className="brief-prose" style={{ marginTop: 18 }}>
        <ReactMarkdown remarkPlugins={[remarkGfm]}>{memo.memoMd}</ReactMarkdown>
      </article>
    </div>
  );
}

function IssueRegister({ issues }: { issues: LegalMemoIssue[] }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
      <div className="eyebrow" style={{ fontSize: 11 }}>
        Top issues ({issues.length})
      </div>
      {issues.slice(0, 8).map((issue) => (
        <div
          key={issue.rank}
          style={{
            border: '1px solid var(--px-border, #e2e2e2)',
            borderLeft: `3px solid ${severityColor(issue.severity)}`,
            background: 'rgba(0,0,0,0.015)',
            borderRadius: 6,
            padding: '10px 12px',
            fontSize: 13,
            lineHeight: 1.5,
          }}
        >
          <div style={{ display: 'flex', alignItems: 'baseline', gap: 8, marginBottom: 4 }}>
            <span
              style={{
                fontSize: 10,
                fontWeight: 700,
                letterSpacing: 0.4,
                padding: '2px 6px',
                borderRadius: 3,
                background: severityColor(issue.severity),
                color: '#fff',
              }}
            >
              {issue.severity}
            </span>
            <span className="mono-label" style={{ fontSize: 10 }}>
              {issue.category}
            </span>
            <strong style={{ fontSize: 13 }}>{issue.title}</strong>
            {issue.where ? (
              <span className="muted" style={{ fontSize: 11, marginLeft: 'auto' }}>
                {issue.where}
              </span>
            ) : null}
          </div>
          <div style={{ marginBottom: issue.non_standard_reason ? 4 : 0 }}>{issue.risk}</div>
          {issue.non_standard_reason ? (
            <div className="muted" style={{ fontSize: 12 }}>
              <em>{issue.non_standard_reason}</em>
            </div>
          ) : null}
        </div>
      ))}
    </div>
  );
}

function severityColor(severity: LegalMemoIssue['severity']): string {
  switch (severity) {
    case 'CRITICAL':
      return '#7d0000';
    case 'HIGH':
      return '#cc3333';
    case 'MEDIUM':
      return '#d4631e';
    case 'LOW':
      return '#1d6f42';
    default:
      return '#666';
  }
}

function RiskScorePill({ score, rating }: { score: number; rating: string | null }) {
  const tone = riskTone(score);
  return (
    <div
      style={{
        display: 'inline-flex',
        alignItems: 'center',
        gap: 8,
        padding: '6px 12px',
        borderRadius: 8,
        background: tone.bg,
        border: `1px solid ${tone.border}`,
        color: tone.fg,
      }}
    >
      <span style={{ fontWeight: 800, fontSize: 18 }}>{Math.round(score)}</span>
      <span style={{ fontSize: 11, opacity: 0.7 }}>/ 100</span>
      {rating ? (
        <span className="mono-label" style={{ fontSize: 10, marginLeft: 4 }}>
          {rating.toUpperCase()}
        </span>
      ) : null}
    </div>
  );
}

function riskTone(score: number) {
  if (score >= 81) return { bg: '#fde2e2', fg: '#7d0000', border: '#cc3333' };
  if (score >= 61) return { bg: '#ffe7d3', fg: '#7a3a00', border: '#d4631e' };
  if (score >= 41) return { bg: '#fff4d1', fg: '#5e4500', border: '#c8a02b' };
  if (score >= 21) return { bg: '#e6f1ff', fg: '#1a3d6b', border: '#3a7bd5' };
  return { bg: '#dff5e7', fg: '#1d6f42', border: '#1d6f42' };
}

function BriefPane({ detail }: { detail: DocumentDetail }) {
  if (!detail.narrativeBriefMd) {
    return (
      <div className="muted brief-empty">
        <div style={{ fontSize: 14, marginBottom: 8 }}>
          No Intelligence Brief yet for this document.
        </div>
        <div style={{ fontSize: 13 }}>
          Click <strong>Refine with LLM</strong> above to generate a sectioned narrative brief
          authored in the voice of a senior healthcare attorney. The brief covers parties,
          economic terms, risk flags, renewal cues, and a plain-English summary.
        </div>
      </div>
    );
  }
  return (
    <article className="brief-prose">
      <ReactMarkdown remarkPlugins={[remarkGfm]}>{detail.narrativeBriefMd}</ReactMarkdown>
    </article>
  );
}

function FieldsPane({
  detail,
  fields,
  hasLlm,
  onCitationClick,
  activeCitation,
}: {
  detail: DocumentDetail;
  fields: ExtractedField[];
  hasLlm: boolean;
  onCitationClick?: (citation: string) => void;
  activeCitation?: string | null;
}) {
  const [showAllFields, setShowAllFields] = useState(false);
  const [showRiskFlags, setShowRiskFlags] = useState(false);

  // Pull risk flags out of the secondary fields list — they get their own
  // collapsible at the bottom and shouldn't dominate the primary view.
  const riskField = fields.find((f) => f.name === 'risk_flags');
  const otherFields = fields.filter((f) => f.name !== 'risk_flags');
  const riskFlags = parseRiskFlags(riskField?.value);

  const headline = detail.headline;
  const citations = detail.fieldCitations ?? {};

  return (
    <div className="fields-pane">
      <div className="eyebrow" style={{ marginBottom: 12, fontSize: 11 }}>
        {hasLlm ? `LLM extracted · ${detail.llmModel ?? ''}` : 'Regex extracted (v1)'}
      </div>

      {headline ? (
        <HeadlineGrid
          headline={headline}
          citations={citations}
          onCitationClick={onCitationClick}
          activeCitation={activeCitation}
        />
      ) : fields.length === 0 ? (
        <div className="muted">
          No structured fields extracted.{' '}
          {detail.extractionStatus === 'no_extractor'
            ? "We don't yet have an extractor for this contract type."
            : 'Try processing again or check that the layout extraction succeeded.'}
        </div>
      ) : null}

      {otherFields.length > 0 ? (
        <div className="collapsible-section">
          <button
            className="collapsible-trigger"
            onClick={() => setShowAllFields((v) => !v)}
            aria-expanded={showAllFields}
          >
            <span>{showAllFields ? '▼' : '▶'}</span>
            <span>Structured details · {otherFields.length} fields</span>
          </button>
          {showAllFields ? (
            <div className="field-grid">
              {otherFields.map((f) => (
                <FieldRow key={f.name} field={f} />
              ))}
            </div>
          ) : null}
        </div>
      ) : null}

      {riskFlags.length > 0 ? (
        <div className="collapsible-section">
          <button
            className="collapsible-trigger"
            onClick={() => setShowRiskFlags((v) => !v)}
            aria-expanded={showRiskFlags}
          >
            <span>{showRiskFlags ? '▼' : '▶'}</span>
            <span>
              Risk flags ·{' '}
              <span className="risk-count-high">
                {riskFlags.filter((r) => r.severity === 'HIGH').length} HIGH
              </span>
              {' · '}
              <span className="risk-count-med">
                {riskFlags.filter((r) => r.severity === 'MED').length} MED
              </span>
              {' · '}
              <span className="risk-count-low">
                {riskFlags.filter((r) => r.severity === 'LOW').length} LOW
              </span>
            </span>
          </button>
          {showRiskFlags ? (
            <div className="risk-flags-list">
              {riskFlags.map((r, i) => (
                <div key={i} className={`risk-flag risk-flag-${r.severity.toLowerCase()}`}>
                  <div className="risk-flag-head">
                    <span className={`risk-badge risk-badge-${r.severity.toLowerCase()}`}>
                      {r.severity}
                    </span>
                    <span className="risk-flag-cat">{r.category}</span>
                  </div>
                  <div className="risk-flag-body">{r.flag}</div>
                  {r.evidence ? (
                    <div className="risk-flag-evidence muted">{r.evidence}</div>
                  ) : null}
                </div>
              ))}
            </div>
          ) : null}
        </div>
      ) : null}

      {detail.extractedFields?.reasonCodes && detail.extractedFields.reasonCodes.length > 0 ? (
        <div style={{ marginTop: 16 }}>
          <div className="eyebrow" style={{ fontSize: 11 }}>Reasoning</div>
          <div className="reason-codes">
            {detail.extractedFields.reasonCodes.map((rc) => (
              <span key={rc} className="reason-pill">{rc}</span>
            ))}
          </div>
        </div>
      ) : null}
    </div>
  );
}

interface RiskFlag {
  severity: 'HIGH' | 'MED' | 'LOW';
  category: string;
  flag: string;
  evidence: string;
}

function parseRiskFlags(raw: string | null | undefined): RiskFlag[] {
  if (!raw) return [];
  try {
    const arr = JSON.parse(raw);
    if (!Array.isArray(arr)) return [];
    return arr
      .filter((x) => x && typeof x === 'object')
      .map((x) => ({
        severity: (x.severity || 'LOW').toUpperCase() as RiskFlag['severity'],
        category: x.category || '',
        flag: x.flag || '',
        evidence: x.evidence || '',
      }));
  } catch {
    return [];
  }
}

function HeadlineGrid({
  headline,
  citations,
  onCitationClick,
  activeCitation,
}: {
  headline: Record<string, string | number | boolean | null>;
  citations: Record<string, string>;
  onCitationClick?: (citation: string) => void;
  activeCitation?: string | null;
}) {
  const entries = Object.entries(headline);
  return (
    <div className="headline-section">
      <div className="eyebrow headline-eyebrow">Canonical Headline</div>
      <div className="headline-grid">
        {entries.map(([key, value]) => {
          const citation = citations[key];
          return (
            <HeadlineCard
              key={key}
              fieldKey={key}
              value={value}
              citation={citation}
              isActive={!!citation && citation === activeCitation}
              onClick={citation && onCitationClick ? () => onCitationClick(citation) : undefined}
            />
          );
        })}
      </div>
    </div>
  );
}

function HeadlineCard({
  fieldKey,
  value,
  citation,
  isActive,
  onClick,
}: {
  fieldKey: string;
  value: string | number | boolean | null;
  citation: string | undefined;
  isActive?: boolean;
  onClick?: () => void;
}) {
  const label = formatLabel(fieldKey);
  const isNull = value === null || value === undefined || value === '';
  const clickable = !!onClick && !!citation;
  const className = [
    'headline-card',
    isNull ? 'is-null' : '',
    clickable ? 'is-clickable' : '',
    isActive ? 'is-active' : '',
  ]
    .filter(Boolean)
    .join(' ');
  const handleClick = () => {
    if (onClick) onClick();
  };
  const Wrapper: any = clickable ? 'button' : 'div';
  const wrapperProps = clickable
    ? {
        type: 'button',
        onClick: handleClick,
        title: 'Click to highlight the source passage on the left',
      }
    : {};
  return (
    <Wrapper className={className} {...wrapperProps}>
      <div className="headline-card-label">{label}</div>
      <div className="headline-card-value">
        {isNull ? <span className="muted">- not stated</span> : formatValue(fieldKey, value)}
      </div>
      {citation ? (
        <div className="headline-card-citation muted" title={citation}>
          {citation.length > 90 ? citation.slice(0, 90) + '...' : citation}
        </div>
      ) : null}
    </Wrapper>
  );
}

function LayoutSnippetPane({
  snippetRef,
  snippet,
  citation,
  flashKey,
}: {
  snippetRef: React.RefObject<HTMLPreElement | null>;
  snippet: string;
  citation: string | null;
  flashKey: number;
}) {
  // Try to locate the citation inside the snippet. Citations from the LLM
  // are typically short quotes (15-150 chars). Match case-insensitively and
  // normalize whitespace, but render the original snippet substring so the
  // user sees the document's actual punctuation/spacing.
  const match = useMemo(() => findCitation(snippet, citation), [snippet, citation]);

  if (!match) {
    return (
      <pre ref={snippetRef} className="layout-snippet">
        {snippet}
      </pre>
    );
  }

  const before = snippet.slice(0, match.start);
  const matched = snippet.slice(match.start, match.end);
  const after = snippet.slice(match.end);
  return (
    <pre ref={snippetRef} className="layout-snippet">
      {before}
      <mark
        key={flashKey /* re-trigger flash animation on each click */}
        data-citation-anchor="true"
        className="citation-mark"
      >
        {matched}
      </mark>
      {after}
    </pre>
  );
}

/**
 * Locate a citation quote inside the layout snippet. The LLM tends to emit
 * a near-verbatim quote with possible whitespace/quote-style differences;
 * this function normalizes both sides to a token sequence and uses indexOf
 * on that, then walks back to the original character offsets.
 */
function findCitation(snippet: string, citation: string | null): { start: number; end: number } | null {
  if (!citation) return null;
  const trimmed = citation.replace(/^[\s"'‘’“”]+|[\s"'‘’“”]+$/g, '');
  if (trimmed.length < 6) return null;

  // Direct case-insensitive match first (cheapest path).
  const lowerSnippet = snippet.toLowerCase();
  const lowerNeedle = trimmed.toLowerCase();
  let idx = lowerSnippet.indexOf(lowerNeedle);
  if (idx !== -1) {
    return { start: idx, end: idx + trimmed.length };
  }

  // Try a shortened head (first 40 chars) — the LLM sometimes paraphrases the
  // tail of long quotes.
  if (trimmed.length > 40) {
    const head = lowerNeedle.slice(0, 40);
    idx = lowerSnippet.indexOf(head);
    if (idx !== -1) {
      return { start: idx, end: idx + 40 };
    }
  }

  // Last resort: walk on collapsed whitespace.
  const norm = (s: string) => s.replace(/\s+/g, ' ').toLowerCase();
  const normSnippet = norm(snippet);
  const normNeedle = norm(trimmed);
  const normIdx = normSnippet.indexOf(normNeedle);
  if (normIdx === -1) return null;
  // Map back: count chars in original snippet until we hit normIdx in the
  // normalized version. Simplistic but workable for short docs.
  let origPos = 0;
  let normPos = 0;
  while (normPos < normIdx && origPos < snippet.length) {
    const orig = snippet[origPos];
    if (/\s/.test(orig)) {
      // collapsed runs of ws are 1 char in normalized
      while (origPos < snippet.length && /\s/.test(snippet[origPos])) origPos++;
      normPos++;
    } else {
      origPos++;
      normPos++;
    }
  }
  // Walk needle length in original.
  let endPos = origPos;
  let used = 0;
  while (used < normNeedle.length && endPos < snippet.length) {
    const ch = snippet[endPos];
    if (/\s/.test(ch)) {
      while (endPos < snippet.length && /\s/.test(snippet[endPos])) endPos++;
      used++;
    } else {
      endPos++;
      used++;
    }
  }
  return { start: origPos, end: endPos };
}

function formatLabel(key: string): string {
  // base_rent_monthly_usd -> "Base Rent (Monthly)"
  // base_rent_per_rsf_yr_usd -> "Base Rent ($/RSF/yr)"
  // operating_cost_treatment -> "Operating Cost Treatment"
  const overrides: Record<string, string> = {
    base_rent_monthly_usd: 'Base Rent (Monthly)',
    base_rent_per_rsf_yr_usd: 'Base Rent ($/RSF/yr)',
    operating_cost_treatment: 'Op-Cost Treatment',
    total_rentable_sqft: 'Total RSF',
    annual_escalation_pct: 'Annual Escalation',
    base_compensation_annual_usd: 'Base Comp (Annual)',
    without_cause_notice_days: 'Without-Cause Notice',
    non_compete_radius_miles: 'Non-Compete Radius',
    non_compete_duration_months: 'Non-Compete Duration',
    confidentiality_survival_months: 'Confidentiality Survival',
    discussion_term_months: 'Discussion Term',
    initial_term_months: 'Initial Term',
    response_time_phone_minutes: 'Phone Response',
    coverage_schedule_summary: 'Coverage Schedule',
    permitted_purpose_quote: 'Permitted Purpose',
    annual_money_flow_usd: 'Annual Money Flow',
    payment_direction: 'Payment Direction',
    subject_matter_summary: 'Subject Matter',
    liability_cap_usd: 'Liability Cap',
    is_baa: 'Is BAA',
    fmv_certified: 'FMV Certified',
    tail_insurance_paid_by: 'Tail Insurance Paid By',
    productivity_model: 'Productivity Model',
    malpractice_provided_by: 'Malpractice Provider',
    counterparty_name: 'Counterparty',
    counterparty_class: 'Counterparty Class',
    has_standstill: 'Standstill Clause',
    has_non_solicitation: 'Non-Solicitation',
    is_mutual: 'Mutual',
    trade_secret_perpetual: 'Trade Secret Perpetual',
    acquirer_signal: 'Acquirer Signal',
    stipend_amount_usd: 'Stipend Amount',
    stipend_basis: 'Stipend Basis',
    is_signed: 'Signed?',
    physician_name: 'Physician',
    employer: 'Employer',
    fte: 'FTE',
    position_title: 'Position',
    document_type: 'Document Type',
    coverage_specialty: 'Specialty',
    covering_group: 'Covering Group',
    covered_facility: 'Covered Facility',
  };
  if (overrides[key]) return overrides[key];
  return key
    .split('_')
    .map((p) => p.charAt(0).toUpperCase() + p.slice(1))
    .join(' ');
}

function formatValue(key: string, value: string | number | boolean): string {
  if (typeof value === 'boolean') return value ? 'Yes' : 'No';
  if (key.endsWith('_usd')) {
    if (typeof value === 'number') {
      const formatted = value.toLocaleString('en-US', {
        style: 'currency',
        currency: 'USD',
        maximumFractionDigits: 2,
      });
      if (key.includes('monthly')) return `${formatted}/mo`;
      if (key.includes('annual') || key.includes('yr')) return `${formatted}/yr`;
      return formatted;
    }
  }
  if (key.endsWith('_pct') && typeof value === 'number') return `${value}%`;
  if (key.endsWith('_sqft') && typeof value === 'number') return `${value.toLocaleString('en-US')} RSF`;
  if (key.endsWith('_months') && typeof value === 'number') {
    const years = value / 12;
    return Number.isInteger(years) ? `${value} months (${years} yr)` : `${value} months`;
  }
  if (key.endsWith('_days') && typeof value === 'number') return `${value} days`;
  if (key.endsWith('_miles') && typeof value === 'number') return `${value} miles`;
  if (key.endsWith('_minutes') && typeof value === 'number') return `${value} min`;
  if (key.endsWith('_date') && typeof value === 'string') {
    const d = new Date(value);
    if (!isNaN(d.getTime())) {
      return d.toLocaleDateString('en-US', { year: 'numeric', month: 'short', day: 'numeric' });
    }
  }
  if (key === 'operating_cost_treatment' && typeof value === 'string') {
    return value.toUpperCase().replace(/_/g, ' ');
  }
  return String(value);
}

function LlmActionRow({
  detail,
  hasLlm,
  onUpdated,
}: {
  detail: DocumentDetail;
  hasLlm: boolean;
  onUpdated: (d: DocumentDetail) => void;
}) {
  const [running, setRunning] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function runLlm() {
    setRunning(true);
    setError(null);
    try {
      await analysisApi.llmExtract(detail.documentAssetId);
      const fresh = await analysisApi.getDocument(detail.documentAssetId);
      onUpdated(fresh);
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'LLM extraction failed.';
      setError(msg);
    } finally {
      setRunning(false);
    }
  }

  return (
    <div className="llm-bar">
      <div>
        {hasLlm ? (
          <span>
            <StatusChip tone="ok">LLM-refined</StatusChip>
            <span className="muted" style={{ marginLeft: 10, fontSize: 12 }}>
              {detail.llmModel} · {detail.llmExtractedAt ? new Date(detail.llmExtractedAt).toLocaleString() : ''}
            </span>
          </span>
        ) : (
          <span className="muted" style={{ fontSize: 13 }}>
            Showing regex v1 extraction. LLM refinement available — typically cleaner parties / dates / rent / signers.
          </span>
        )}
      </div>
      <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
        {error ? <span className="mono-label" style={{ color: 'var(--px-orange)' }}>{error}</span> : null}
        <Button onClick={runLlm} disabled={running} variant={hasLlm ? 'secondary' : undefined}>
          {running ? 'Running LLM…' : hasLlm ? 'Re-run LLM' : 'Refine with LLM'}
        </Button>
      </div>
    </div>
  );
}

function FieldRow({ field }: { field: ExtractedField }) {
  const value = field.value;
  let body: ReactElement;

  if (value === null || value === '' || value === '"— not found"' || value === 'null') {
    body = <span className="muted">— not found</span>;
  } else {
    body = <FieldValue raw={value} />;
  }

  return (
    <div className="field-card">
      <div className="field-card-head">
        <div className="field-name">{prettyFieldName(field.name)}</div>
        <div className="field-confidence">
          <ConfidenceBar value={field.confidence} />
        </div>
      </div>
      <div className="field-card-body">{body}</div>
      {field.sourceCitation ? (
        <div className="field-citation muted">{field.sourceCitation}</div>
      ) : null}
    </div>
  );
}

function FieldValue({ raw }: { raw: string }) {
  // Server stringifies JsonElement values into JSON. Try to parse and render
  // structured; fall back to the raw string for primitives.
  let parsed: unknown = raw;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return <span>{raw}</span>;
  }

  if (parsed === null) return <span className="muted">— not found</span>;
  if (typeof parsed === 'string') return <span>{parsed || <span className="muted">— empty</span>}</span>;
  if (typeof parsed === 'number' || typeof parsed === 'boolean') return <span>{String(parsed)}</span>;

  if (Array.isArray(parsed)) {
    if (parsed.length === 0) return <span className="muted">— empty list</span>;
    return (
      <div className="value-list">
        {parsed.map((item, i) => (
          <div className="value-list-item" key={i}>
            <ValueObject value={item} />
          </div>
        ))}
      </div>
    );
  }

  if (typeof parsed === 'object') {
    return <ValueObject value={parsed} />;
  }

  return <span>{String(parsed)}</span>;
}

function ValueObject({ value }: { value: unknown }) {
  if (value === null) return <span className="muted">— null</span>;
  if (typeof value !== 'object' || Array.isArray(value)) {
    return <FieldValue raw={JSON.stringify(value)} />;
  }
  const entries = Object.entries(value as Record<string, unknown>).filter(
    ([, v]) => v !== null && v !== '' && v !== undefined,
  );
  if (entries.length === 0) return <span className="muted">— empty</span>;
  return (
    <div className="value-object">
      {entries.map(([k, v]) => (
        <div className="value-object-row" key={k}>
          <span className="value-object-key">{prettyFieldName(k)}</span>
          <span className="value-object-val">
            {typeof v === 'object' ? <ValueObject value={v} /> : String(v)}
          </span>
        </div>
      ))}
    </div>
  );
}

function prettyFieldName(name: string): string {
  return name
    .replace(/([A-Z])/g, ' $1')
    .replace(/_/g, ' ')
    .replace(/\s+/g, ' ')
    .trim()
    .replace(/\b\w/g, (c) => c.toUpperCase());
}
