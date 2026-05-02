import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { Card, ConfidenceBar, StatusChip } from '@practicex/design-system';
import {
  analysisApi,
  type ReviewQueueItem,
  readableCandidateType,
  readableRelativeTime,
} from '../lib/api';

type LoadState =
  | { kind: 'loading' }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; items: ReviewQueueItem[] };

export function ReviewQueuePage() {
  const [state, setState] = useState<LoadState>({ kind: 'loading' });

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const items = await analysisApi.getReviewQueue();
        if (!cancelled) setState({ kind: 'ready', items });
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
    return <div className="page"><div className="page-subtitle">Loading review queue…</div></div>;
  }
  if (state.kind === 'error') {
    return <div className="page"><div className="banner banner-error">{state.message}</div></div>;
  }

  const { items } = state;

  return (
    <div className="page">
      <div className="crumb">
        <span>PracticeX</span>
        <span>›</span>
        <span>Review queue</span>
      </div>
      <header className="page-head">
        <div>
          <div className="eyebrow">
            <span className="eyebrow-dot" />
            Human QA · {items.length} document{items.length === 1 ? '' : 's'} awaiting review
          </div>
          <h1 className="page-title">Review extracted fields</h1>
          <div className="page-subtitle">
            Candidates flagged for manual confirmation before they're promoted to canonical contracts.
          </div>
        </div>
      </header>

      {items.length === 0 ? (
        <Card title="Queue empty">
          <div className="muted">
            No documents currently flagged for review. Newly-ingested high-confidence candidates with
            signatures auto-route here; the rest land as <code>candidate</code> and skip review.
          </div>
        </Card>
      ) : (
        <Card title={`Queue · ${items.length}`}>
          <div className="doc-table">
            {items.map((item) => (
              <Link
                key={item.candidateId}
                to={`/portfolio/${item.documentAssetId}`}
                className="doc-row"
                style={{ textDecoration: 'none', gridTemplateColumns: 'minmax(0, 1.6fr) minmax(0, 1.4fr) 110px 100px 110px' }}
              >
                <div className="doc-row-name" title={item.fileName}>{item.fileName}</div>
                <div className="doc-row-type">
                  {readableCandidateType(item.candidateType)}
                  {item.extractedSubtype ? <span className="muted"> · {item.extractedSubtype}</span> : null}
                </div>
                <div className="doc-row-meta">{readableRelativeTime(item.createdAt)}</div>
                <div style={{ alignItems: 'center', display: 'flex', gap: 8 }}>
                  <ConfidenceBar value={Number(item.confidence) * 100} />
                  <span className="mono-label">{Math.round(Number(item.confidence) * 100)}%</span>
                </div>
                <div>
                  {item.usedDocIntelligence ? <StatusChip tone="accent">OCR'd</StatusChip> : <StatusChip tone="ok">digital</StatusChip>}
                </div>
              </Link>
            ))}
          </div>
        </Card>
      )}
    </div>
  );
}
