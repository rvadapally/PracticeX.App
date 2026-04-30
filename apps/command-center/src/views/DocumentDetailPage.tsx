import { useEffect, useMemo, useState, type ReactElement } from 'react';
import { Link, useParams } from 'react-router-dom';
import { Button, Card, ConfidenceBar, StatusChip } from '@practicex/design-system';
import {
  analysisApi,
  type DocumentDetail,
  type ExtractedField,
  readableCandidateType,
} from '../lib/api';

const API_BASE = (import.meta.env.VITE_API_BASE as string | undefined) ?? '/api';

type LoadState =
  | { kind: 'loading' }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; detail: DocumentDetail };

export function DocumentDetailPage() {
  const { assetId } = useParams<{ assetId: string }>();
  const [state, setState] = useState<LoadState>({ kind: 'loading' });

  useEffect(() => {
    if (!assetId) return;
    let cancelled = false;
    (async () => {
      try {
        const detail = await analysisApi.getDocument(assetId);
        if (!cancelled) setState({ kind: 'ready', detail });
      } catch (err) {
        if (cancelled) return;
        const message = err instanceof Error ? err.message : 'Failed to load document.';
        setState({ kind: 'error', message });
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [assetId]);

  const sourceUrl = useMemo(() => {
    if (!assetId) return null;
    return `${API_BASE}/analysis/documents/${assetId}/content`;
  }, [assetId]);

  if (state.kind === 'loading') {
    return <div className="page"><div className="page-subtitle">Loading document…</div></div>;
  }
  if (state.kind === 'error') {
    return <div className="page"><div className="banner banner-error">{state.message}</div></div>;
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
        <Card title="Original document" className="document-source-card">
          {sourceUrl ? (
            isPdf ? (
              <object
                data={sourceUrl}
                type="application/pdf"
                className="document-source-frame"
              >
                <iframe title="Original PDF" src={sourceUrl} className="document-source-frame" />
              </object>
            ) : (
              <div className="document-source-fallback">
                <p className="muted" style={{ marginBottom: 12 }}>
                  Browsers can't render this format inline.{' '}
                  <a href={sourceUrl} target="_blank" rel="noreferrer">Open the original</a> in a new tab,
                  or read the extracted text below.
                </p>
                {detail.layoutSnippet ? (
                  <pre className="layout-snippet">{detail.layoutSnippet}</pre>
                ) : (
                  <div className="muted">No text snippet available yet — re-process this document to populate.</div>
                )}
              </div>
            )
          ) : (
            <div className="muted">No source URL.</div>
          )}
        </Card>

        <Card
          eyebrow={hasLlm ? `LLM extracted · ${detail.llmModel ?? ''}` : 'Regex extracted (v1)'}
          title={`Extracted fields · ${fields.length}`}
          className="document-fields-card">
          {fields.length === 0 ? (
            <div className="muted">
              No structured fields extracted.{' '}
              {detail.extractionStatus === 'no_extractor'
                ? "We don't yet have an extractor for this contract type."
                : 'Try processing again or check that the layout extraction succeeded.'}
            </div>
          ) : (
            <div className="field-grid">
              {fields.map((f) => (
                <FieldRow key={f.name} field={f} />
              ))}
            </div>
          )}
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
        </Card>
      </section>
    </div>
  );
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

  if (value === null || value === '' || value === '"— not found"') {
    body = <span className="muted">— not found</span>;
  } else {
    body = <FieldValue raw={value} />;
  }

  return (
    <div className="field-row">
      <div className="field-name">{prettyFieldName(field.name)}</div>
      <div className="field-value">{body}</div>
      <div className="field-confidence">
        <ConfidenceBar value={field.confidence} />
      </div>
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
