import { Button, ConfidenceBar, KpiCard, StatusChip } from '@practicex/design-system';
import { ArrowLeft, ArrowRight, FolderUp, Loader2, Sparkles, Upload, X } from 'lucide-react';
import { useCallback, useMemo, useRef, useState } from 'react';
import {
  type IngestionBatchSummary,
  type ManifestItem,
  type ManifestScanResponse,
  type ManifestScoredItem,
  readableCandidateType,
  readableReason,
  sourcesApi,
} from '../../lib/api';
import { readFileList, walkDataTransfer, type QueuedFile } from '../../lib/folder-traverse';

type Step = 'select' | 'inventory' | 'prune' | 'process' | 'done';

interface Props {
  connectionId: string;
  onClose: () => void;
  onComplete: (summary: IngestionBatchSummary) => void;
}

export function FolderInventoryWorkflow({ connectionId, onClose, onComplete }: Props) {
  const [step, setStep] = useState<Step>('select');
  const [files, setFiles] = useState<QueuedFile[]>([]);
  const [manifest, setManifest] = useState<ManifestScanResponse | null>(null);
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [batchSummary, setBatchSummary] = useState<IngestionBatchSummary | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const folderInputRef = useRef<HTMLInputElement>(null);

  const addFiles = useCallback((list: FileList | null) => {
    const next = readFileList(list);
    if (next.length === 0) return;
    setFiles((prev) => [...prev, ...next]);
  }, []);

  const handleDrop = useCallback((event: React.DragEvent<HTMLDivElement>) => {
    event.preventDefault();
    void walkDataTransfer(event.dataTransfer).then((collected) => {
      if (collected.length > 0) setFiles((prev) => [...prev, ...collected]);
    });
  }, []);

  const runManifest = useCallback(async () => {
    if (files.length === 0) return;
    setStep('inventory');
    setBusy(true);
    setError(null);
    try {
      const items: ManifestItem[] = files.map((f) => ({
        relativePath: f.relativePath,
        name: f.file.name,
        sizeBytes: f.file.size,
        lastModifiedUtc: new Date(f.file.lastModified).toISOString(),
        mimeType: f.file.type || null,
      }));
      const result = await sourcesApi.scanManifest(connectionId, items);
      setManifest(result);
      // Default selection: everything in Strong + Likely bands.
      const defaults = new Set(
        result.items
          .filter((i) => i.band === 'strong' || i.band === 'likely')
          .map((i) => i.manifestItemId),
      );
      setSelectedIds(defaults);
      setStep('prune');
    } catch (err) {
      const detail = (err as { detail?: string; title?: string }).detail
        ?? (err as { title?: string }).title
        ?? 'Manifest scan failed.';
      setError(detail);
      setStep('select');
    } finally {
      setBusy(false);
    }
  }, [files, connectionId]);

  const toggle = useCallback((manifestItemId: string, allowed: boolean) => {
    if (!allowed) return;
    setSelectedIds((prev) => {
      const next = new Set(prev);
      if (next.has(manifestItemId)) next.delete(manifestItemId); else next.add(manifestItemId);
      return next;
    });
  }, []);

  const runUpload = useCallback(async () => {
    if (!manifest || selectedIds.size === 0) return;
    setStep('process');
    setBusy(true);
    setError(null);
    try {
      const byManifestId = new Map(manifest.items.map((i) => [i.manifestItemId, i]));
      const fileByPath = new Map(files.map((f) => [f.relativePath, f]));
      const selected = Array.from(selectedIds)
        .map((id) => {
          const scored = byManifestId.get(id);
          if (!scored) return null;
          const queued = fileByPath.get(scored.relativePath);
          if (!queued) return null;
          return { ...queued, manifestItemId: id };
        })
        .filter((v): v is QueuedFile & { manifestItemId: string } => v !== null);

      const summary = await sourcesApi.uploadBundle(connectionId, manifest.batchId, selected);
      setBatchSummary(summary);
      setStep('done');
      onComplete(summary);
    } catch (err) {
      const detail = (err as { detail?: string; title?: string }).detail
        ?? (err as { title?: string }).title
        ?? 'Bundle upload failed.';
      setError(detail);
      setStep('prune');
    } finally {
      setBusy(false);
    }
  }, [manifest, selectedIds, files, connectionId, onComplete]);

  const totalSelectedBytes = useMemo(() => {
    if (!manifest) return 0;
    const m = new Map(manifest.items.map((i) => [i.manifestItemId, i]));
    return Array.from(selectedIds).reduce((sum, id) => sum + (m.get(id)?.sizeBytes ?? 0), 0);
  }, [selectedIds, manifest]);

  return (
    <div role="dialog" aria-modal="true" style={overlayStyle}>
      <div style={dialogStyle}>
        <header style={headerStyle}>
          <div>
            <div className="mono-label">Source discovery · staged scan</div>
            <h2 style={{ fontFamily: 'var(--px-serif)', fontSize: 24, margin: '4px 0 0' }}>
              {step === 'select' && 'Select files'}
              {step === 'inventory' && 'Scanning manifest…'}
              {step === 'prune' && 'Review what will be processed'}
              {step === 'process' && 'Uploading selected files…'}
              {step === 'done' && 'Scan complete'}
            </h2>
            <Stepper step={step} />
          </div>
          <button className="px-icon-button" type="button" onClick={onClose} aria-label="Close">
            <X size={14} />
          </button>
        </header>

        {error ? <ErrorBanner detail={error} /> : null}

        {step === 'select' && (
          <SelectStep
            files={files}
            setFiles={setFiles}
            addFiles={addFiles}
            handleDrop={handleDrop}
            fileInputRef={fileInputRef}
            folderInputRef={folderInputRef}
          />
        )}

        {step === 'inventory' && (
          <div style={busyStyle}>
            <Loader2 className="spin" size={28} />
            <strong>Scoring metadata for {files.length} file{files.length === 1 ? '' : 's'}…</strong>
            <div className="muted">No bytes leave the browser yet — only filename, path, size, and modified date.</div>
          </div>
        )}

        {step === 'prune' && manifest && (
          <PruneStep
            manifest={manifest}
            selectedIds={selectedIds}
            setSelectedIds={setSelectedIds}
            toggle={toggle}
          />
        )}

        {step === 'process' && (
          <div style={busyStyle}>
            <Loader2 className="spin" size={28} />
            <strong>Uploading {selectedIds.size} file{selectedIds.size === 1 ? '' : 's'}…</strong>
            <div className="muted">Hashing, validating, and routing each document.</div>
          </div>
        )}

        {step === 'done' && batchSummary && (
          <DoneStep summary={batchSummary} avoided={(manifest?.totalItems ?? 0) - selectedIds.size} />
        )}

        <footer style={footerStyle}>
          {step === 'select' && (
            <>
              <Button variant="secondary" onClick={onClose} disabled={busy}>Cancel</Button>
              <Button onClick={runManifest} disabled={busy || files.length === 0}>
                <Sparkles size={14} /> Scan {files.length} file{files.length === 1 ? '' : 's'}
                <ArrowRight size={14} />
              </Button>
            </>
          )}

          {step === 'prune' && manifest && (
            <>
              <Button variant="secondary" onClick={() => setStep('select')} disabled={busy}>
                <ArrowLeft size={14} /> Reselect
              </Button>
              <Button onClick={runUpload} disabled={busy || selectedIds.size === 0}>
                <Upload size={14} /> Process {selectedIds.size} selected
                {totalSelectedBytes > 0 ? ` (${formatBytes(totalSelectedBytes)})` : ''}
              </Button>
            </>
          )}

          {step === 'done' && (
            <Button onClick={onClose}>Close</Button>
          )}
        </footer>
      </div>
    </div>
  );
}

function Stepper({ step }: { step: Step }) {
  const order: Step[] = ['select', 'inventory', 'prune', 'process', 'done'];
  const currentIdx = order.indexOf(step);
  const labels: Record<Step, string> = {
    select: '1 · Select',
    inventory: '2 · Inventory',
    prune: '3 · Prune',
    process: '4 · Process',
    done: '5 · Done',
  };
  return (
    <div style={{ display: 'flex', gap: 8, marginTop: 8, flexWrap: 'wrap' }}>
      {order.map((s, i) => (
        <span
          key={s}
          className="mono-label"
          style={{
            color: i <= currentIdx ? 'var(--px-text)' : 'var(--px-text-muted)',
            opacity: i <= currentIdx ? 1 : 0.6,
          }}
        >
          {labels[s]}
        </span>
      ))}
    </div>
  );
}

function SelectStep({
  files,
  setFiles,
  addFiles,
  handleDrop,
  fileInputRef,
  folderInputRef,
}: {
  files: QueuedFile[];
  setFiles: React.Dispatch<React.SetStateAction<QueuedFile[]>>;
  addFiles: (list: FileList | null) => void;
  handleDrop: (event: React.DragEvent<HTMLDivElement>) => void;
  fileInputRef: React.RefObject<HTMLInputElement | null>;
  folderInputRef: React.RefObject<HTMLInputElement | null>;
}) {
  return (
    <>
      <div onDragOver={(e) => e.preventDefault()} onDrop={handleDrop} style={dropZoneStyle}>
        <FolderUp size={22} />
        <strong>Drop a folder here</strong>
        <div className="muted">PracticeX will scan filenames and folder paths locally before deciding what to upload.</div>
        <div style={{ display: 'flex', gap: 8, marginTop: 10 }}>
          <Button variant="secondary" onClick={() => folderInputRef.current?.click()}>
            <FolderUp size={14} /> Pick folder
          </Button>
          <Button variant="secondary" onClick={() => fileInputRef.current?.click()}>
            <Upload size={14} /> Pick files
          </Button>
        </div>
        <input
          ref={folderInputRef}
          type="file"
          {...({ webkitdirectory: '', directory: '' } as Record<string, string>)}
          multiple
          hidden
          onChange={(e) => addFiles(e.currentTarget.files)}
        />
        <input
          ref={fileInputRef}
          type="file"
          multiple
          hidden
          onChange={(e) => addFiles(e.currentTarget.files)}
        />
      </div>

      {files.length > 0 ? (
        <div style={{ marginTop: 18 }}>
          <div className="mono-label">{files.length} file{files.length === 1 ? '' : 's'} queued</div>
          <div style={{ maxHeight: 200, overflowY: 'auto', marginTop: 8 }}>
            {files.map((entry, idx) => (
              <div key={`${entry.relativePath}-${idx}`} style={fileRowStyle}>
                <span style={{ flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {entry.relativePath}
                </span>
                <span className="mono-label">{(entry.file.size / 1024).toFixed(1)} KB</span>
                <button
                  className="px-button ghost"
                  type="button"
                  onClick={() => setFiles((prev) => prev.filter((_, i) => i !== idx))}
                >
                  <X size={12} />
                </button>
              </div>
            ))}
          </div>
        </div>
      ) : null}
    </>
  );
}

function PruneStep({
  manifest,
  selectedIds,
  setSelectedIds,
  toggle,
}: {
  manifest: ManifestScanResponse;
  selectedIds: Set<string>;
  setSelectedIds: React.Dispatch<React.SetStateAction<Set<string>>>;
  toggle: (id: string, allowed: boolean) => void;
}) {
  const grouped = useMemo(() => {
    const out: Record<string, ManifestScoredItem[]> = { strong: [], likely: [], possible: [], skipped: [] };
    manifest.items.forEach((i) => out[i.band]?.push(i));
    Object.values(out).forEach((arr) => arr.sort((a, b) => b.confidence - a.confidence));
    return out;
  }, [manifest]);

  const avoidedPct = manifest.totalItems > 0
    ? Math.round((manifest.skippedCount / manifest.totalItems) * 100)
    : 0;

  const eligibleIds = useMemo(
    () => manifest.items.filter((i) => i.band !== 'skipped').map((i) => i.manifestItemId),
    [manifest],
  );
  const strongLikelyIds = useMemo(
    () => manifest.items.filter((i) => i.band === 'strong' || i.band === 'likely').map((i) => i.manifestItemId),
    [manifest],
  );

  const selectAllEligible = useCallback(() => setSelectedIds(new Set(eligibleIds)), [eligibleIds, setSelectedIds]);
  const selectStrongLikely = useCallback(() => setSelectedIds(new Set(strongLikelyIds)), [strongLikelyIds, setSelectedIds]);
  const clearAll = useCallback(() => setSelectedIds(new Set()), [setSelectedIds]);

  const setBand = useCallback(
    (band: 'strong' | 'likely' | 'possible' | 'skipped', selected: boolean) => {
      if (band === 'skipped') return;
      const ids = grouped[band].map((i) => i.manifestItemId);
      setSelectedIds((prev) => {
        const next = new Set(prev);
        if (selected) ids.forEach((id) => next.add(id));
        else ids.forEach((id) => next.delete(id));
        return next;
      });
    },
    [grouped, setSelectedIds],
  );

  return (
    <div>
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, minmax(0, 1fr))', gap: 10, marginTop: 4 }}>
        <KpiCard label="Strong" value={manifest.strongCount} helper="≥ 80% confidence" tone="accent" />
        <KpiCard label="Likely" value={manifest.likelyCount} helper="60–79%" tone="accent" />
        <KpiCard label="Possible" value={manifest.possibleCount} helper="35–59%" tone="default" />
        <KpiCard label="Skipped" value={manifest.skippedCount} helper={`${avoidedPct}% avoided`} tone="warn" />
      </div>

      <div
        style={{
          alignItems: 'center',
          background: 'var(--px-surface-2)',
          border: '1px solid var(--px-line)',
          borderRadius: 'var(--px-radius)',
          display: 'flex',
          flexWrap: 'wrap',
          gap: 8,
          justifyContent: 'space-between',
          marginTop: 12,
          padding: '8px 12px',
        }}
      >
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
          <button type="button" className="px-button ghost" onClick={selectStrongLikely} style={pillStyle}>
            Strong + Likely ({strongLikelyIds.length})
          </button>
          <button type="button" className="px-button ghost" onClick={selectAllEligible} style={pillStyle}>
            All eligible ({eligibleIds.length})
          </button>
          <button type="button" className="px-button ghost" onClick={clearAll} style={pillStyle}>
            Clear
          </button>
        </div>
        <div className="mono-label">
          {selectedIds.size} of {eligibleIds.length} selected
        </div>
      </div>

      <div style={{ marginTop: 12, maxHeight: '46vh', overflowY: 'auto', borderTop: '1px solid var(--px-line-2)' }}>
        {(['strong', 'likely', 'possible', 'skipped'] as const).map((band) => {
          const list = grouped[band];
          if (!list || list.length === 0) return null;
          const allowed = band !== 'skipped';
          const selectedInBand = list.filter((i) => selectedIds.has(i.manifestItemId)).length;
          const allSelectedInBand = allowed && selectedInBand === list.length && list.length > 0;
          const someSelectedInBand = allowed && selectedInBand > 0 && selectedInBand < list.length;
          return (
            <div key={band} style={{ marginTop: 12 }}>
              <div
                style={{
                  alignItems: 'center',
                  display: 'flex',
                  gap: 10,
                  justifyContent: 'space-between',
                  marginBottom: 6,
                }}
              >
                <div className="mono-label">
                  {labelForBand(band)} · {list.length}
                  {allowed ? ` · ${selectedInBand} selected` : ''}
                </div>
                {allowed ? (
                  <button
                    type="button"
                    className="px-button ghost"
                    onClick={() => setBand(band, !allSelectedInBand)}
                    style={pillStyle}
                    aria-pressed={allSelectedInBand}
                  >
                    {allSelectedInBand
                      ? 'Deselect all in band'
                      : someSelectedInBand
                        ? `Select remaining (${list.length - selectedInBand})`
                        : `Select all in band (${list.length})`}
                  </button>
                ) : null}
              </div>
              {list.map((item) => {
                const checked = selectedIds.has(item.manifestItemId);
                return (
                  <label
                    key={item.manifestItemId}
                    style={{
                      display: 'grid',
                      gridTemplateColumns: '24px 1fr 110px 120px',
                      gap: 12,
                      alignItems: 'center',
                      padding: '8px 4px',
                      borderTop: '1px solid var(--px-line-2)',
                      cursor: allowed ? 'pointer' : 'default',
                      opacity: allowed ? 1 : 0.55,
                    }}
                  >
                    <input
                      type="checkbox"
                      checked={checked}
                      disabled={!allowed}
                      onChange={() => toggle(item.manifestItemId, allowed)}
                    />
                    <div style={{ minWidth: 0 }}>
                      <div style={{ fontWeight: 500, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                        {item.relativePath}
                      </div>
                      <div className="muted" style={{ fontSize: 12 }}>
                        {readableCandidateType(item.candidateType)}
                        {item.counterpartyHint ? ` · ${item.counterpartyHint}` : ''}
                        {item.reasonCodes.length > 0 ? ' · ' : ''}
                        {item.reasonCodes.slice(0, 3).map(readableReason).join(' · ')}
                      </div>
                    </div>
                    <ConfidenceBar value={Math.round(item.confidence * 100)} tone={confidenceTone(band)} />
                    <StatusChip tone={chipTone(band)}>{labelForBand(band)}</StatusChip>
                  </label>
                );
              })}
            </div>
          );
        })}
      </div>
    </div>
  );
}

function DoneStep({ summary, avoided }: { summary: IngestionBatchSummary; avoided: number }) {
  return (
    <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, minmax(0, 1fr))', gap: 10, marginTop: 4 }}>
      <KpiCard label="Uploaded" value={summary.fileCount - summary.skippedCount} tone="accent" />
      <KpiCard label="Candidates" value={summary.candidateCount} tone="accent" />
      <KpiCard label="Skipped" value={summary.skippedCount} tone="default" />
      <KpiCard label="Files avoided" value={Math.max(0, avoided)} helper="Pruned before upload" tone="warn" />
    </div>
  );
}

function ErrorBanner({ detail }: { detail: string }) {
  return (
    <div
      style={{
        background: 'var(--px-orange-soft)',
        borderRadius: 'var(--px-radius)',
        color: 'var(--px-orange)',
        fontSize: 12.5,
        padding: 10,
        marginTop: 12,
      }}
    >
      {detail}
    </div>
  );
}

function labelForBand(band: string): string {
  switch (band) {
    case 'strong': return 'Strong';
    case 'likely': return 'Likely';
    case 'possible': return 'Possible';
    default: return 'Skipped';
  }
}

function chipTone(band: string): 'ok' | 'accent' | 'warn' | 'muted' {
  switch (band) {
    case 'strong': return 'ok';
    case 'likely': return 'accent';
    case 'possible': return 'muted';
    default: return 'warn';
  }
}

function confidenceTone(band: string): 'ok' | 'accent' | 'warn' {
  switch (band) {
    case 'strong': return 'ok';
    case 'likely': return 'accent';
    case 'possible': return 'warn';
    default: return 'warn';
  }
}

function formatBytes(n: number): string {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  if (n < 1024 * 1024 * 1024) return `${(n / 1024 / 1024).toFixed(1)} MB`;
  return `${(n / 1024 / 1024 / 1024).toFixed(2)} GB`;
}

const overlayStyle: React.CSSProperties = {
  alignItems: 'center',
  background: 'rgba(30, 42, 26, 0.42)',
  bottom: 0,
  display: 'flex',
  justifyContent: 'center',
  left: 0,
  position: 'fixed',
  right: 0,
  top: 0,
  zIndex: 100,
};

const dialogStyle: React.CSSProperties = {
  background: 'var(--px-surface)',
  border: '1px solid var(--px-line)',
  borderRadius: 'var(--px-radius-lg)',
  maxHeight: '88vh',
  overflowY: 'auto',
  padding: 22,
  width: 760,
  display: 'flex',
  flexDirection: 'column',
  gap: 12,
};

const headerStyle: React.CSSProperties = {
  alignItems: 'flex-start',
  display: 'flex',
  justifyContent: 'space-between',
  marginBottom: 4,
};

const dropZoneStyle: React.CSSProperties = {
  alignItems: 'center',
  background: 'var(--px-surface-2)',
  border: '1px dashed var(--px-line)',
  borderRadius: 'var(--px-radius)',
  display: 'flex',
  flexDirection: 'column',
  gap: 6,
  padding: 22,
  textAlign: 'center',
};

const fileRowStyle: React.CSSProperties = {
  alignItems: 'center',
  borderTop: '1px solid var(--px-line-2)',
  display: 'flex',
  fontSize: 12.5,
  gap: 10,
  padding: '8px 4px',
};

const busyStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flexDirection: 'column',
  gap: 8,
  padding: 24,
  textAlign: 'center',
};

const pillStyle: React.CSSProperties = {
  background: 'var(--px-surface)',
  border: '1px solid var(--px-line)',
  borderRadius: 'var(--px-radius)',
  cursor: 'pointer',
  fontFamily: 'var(--px-mono, monospace)',
  fontSize: 11.5,
  letterSpacing: '0.04em',
  padding: '4px 10px',
  textTransform: 'uppercase',
};

const footerStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  gap: 10,
  justifyContent: 'flex-end',
  marginTop: 8,
  borderTop: '1px solid var(--px-line-2)',
  paddingTop: 12,
};
