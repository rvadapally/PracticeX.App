import { useEffect, useMemo, useRef, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { Card } from '@practicex/design-system';
import { DataSet } from 'vis-data/peer';
import { Network, type Options } from 'vis-network/peer';
import 'vis-network/styles/vis-network.css';
import { analysisApi, type EntityGraph, type EntityGraphLink, type EntityGraphNode } from '../lib/api';
import { logEvent } from '../lib/analytics';

type LoadState =
  | { kind: 'loading' }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; graph: EntityGraph };

const TYPE_COLORS: Record<string, { background: string; border: string; highlight: string }> = {
  person: { background: '#fef3c7', border: '#d97706', highlight: '#fbbf24' },
  organization: { background: '#dbeafe', border: '#1d4ed8', highlight: '#3b82f6' },
  asset: { background: '#dcfce7', border: '#15803d', highlight: '#22c55e' },
  document: { background: '#f4f4f4', border: '#737373', highlight: '#525252' },
};

const TYPE_LABELS: Record<string, string> = {
  person: 'People',
  organization: 'Organizations',
  asset: 'Premises / Assets',
  document: 'Documents',
};

export function EntityGraphPage() {
  const [state, setState] = useState<LoadState>({ kind: 'loading' });
  const [filterTypes, setFilterTypes] = useState<Set<string>>(
    new Set(['person', 'organization', 'asset', 'document']),
  );
  const [selected, setSelected] = useState<EntityGraphNode | null>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const networkRef = useRef<Network | null>(null);
  const navigate = useNavigate();

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const graph = await analysisApi.getEntityGraph();
        if (!cancelled) setState({ kind: 'ready', graph });
      } catch (err) {
        if (cancelled) return;
        const detail =
          (err as { detail?: string } | undefined)?.detail ??
          (err as Error)?.message ??
          'Failed to load entity graph.';
        setState({ kind: 'error', message: detail });
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  const filtered = useMemo(() => {
    if (state.kind !== 'ready') return null;
    // Keep all nodes/edges in the dataset — flag visibility so we can dim
    // (instead of remove) filtered-out types. This preserves layout stability
    // across chip toggles and keeps "bridge" nodes visible as faint context.
    const isActive = (n: EntityGraphNode) => filterTypes.has(n.type);
    const nodeActiveById = new Map<string, boolean>();
    state.graph.nodes.forEach((n) => nodeActiveById.set(n.id, isActive(n)));
    return {
      nodes: state.graph.nodes,
      links: state.graph.links,
      isNodeActive: (id: string) => nodeActiveById.get(id) ?? false,
    };
  }, [state, filterTypes]);

  // Build / refresh the network. Using vis-network's imperative API since
  // there's no first-party React wrapper we want to pull in.
  useEffect(() => {
    if (!containerRef.current || !filtered) return;

    const dimNodeColor = (n: EntityGraphNode) => {
      const c = TYPE_COLORS[n.type] ?? TYPE_COLORS.document;
      return {
        background: dim(c.background, 0.85),
        border: dim(c.border, 0.7),
        highlight: c.highlight,
      };
    };

    const nodes = new DataSet(
      filtered.nodes.map((n) => {
        const active = filtered.isNodeActive(n.id);
        return {
          id: n.id,
          label: truncate(n.label, 38),
          title: n.label,
          shape: n.type === 'document' ? 'box' : 'dot',
          size: n.size,
          color: active ? (TYPE_COLORS[n.type] ?? TYPE_COLORS.document) : dimNodeColor(n),
          font: {
            size: n.type === 'document' ? 11 : 12.5,
            color: active ? '#1f1f1f' : '#bdbdbd',
            face: 'Inter, system-ui, sans-serif',
          },
          borderWidth: active ? 1.5 : 0.6,
          _payload: n,
        };
      }),
    );

    const edges = new DataSet(
      filtered.links.map((l, i) => {
        const bothActive = filtered.isNodeActive(l.source) && filtered.isNodeActive(l.target);
        const inferred = l.inferred === true;
        return {
          id: `e${i}`,
          from: l.source,
          to: l.target,
          label: bothActive ? l.relation : '',
          font: {
            size: 9,
            color: '#737373',
            face: 'ui-monospace, monospace',
            background: 'rgba(255,255,255,0.7)',
            strokeWidth: 0,
          },
          color: bothActive
            ? (inferred
                ? { color: '#fbbf24', highlight: '#d4631e', hover: '#fde68a' }
                : { color: '#d4d4d4', highlight: '#d4631e', hover: '#a3a3a3' })
            : { color: 'rgba(212,212,212,0.18)', highlight: '#d4631e', hover: 'rgba(163,163,163,0.3)' },
          dashes: inferred ? [4, 4] : false,
          smooth: { enabled: true, type: 'continuous', roundness: 0.5 },
          arrows: inferred ? '' : 'to',
          width: inferred ? 1.4 : 1,
        };
      }),
    );

    const options: Options = {
      autoResize: true,
      physics: {
        enabled: true,
        solver: 'forceAtlas2Based',
        forceAtlas2Based: {
          gravitationalConstant: -42,
          centralGravity: 0.005,
          springLength: 130,
          springConstant: 0.08,
          damping: 0.6,
        },
        stabilization: { iterations: 240, fit: true },
      },
      interaction: {
        hover: true,
        tooltipDelay: 150,
        navigationButtons: false,
        keyboard: true,
      },
      nodes: { borderWidthSelected: 3 },
      edges: { selectionWidth: 2 },
    };

    if (networkRef.current) {
      networkRef.current.destroy();
    }

    const network = new Network(containerRef.current, { nodes, edges }, options);
    networkRef.current = network;

    network.on('selectNode', (params: { nodes: string[] }) => {
      const id = params.nodes?.[0];
      if (!id) return;
      const item = nodes.get(id) as { _payload?: EntityGraphNode } | null;
      if (item?._payload) setSelected(item._payload);
    });
    network.on('doubleClick', (params: { nodes: string[] }) => {
      const id = params.nodes?.[0];
      if (!id) return;
      const item = nodes.get(id) as { _payload?: EntityGraphNode } | null;
      if (item?._payload?.type === 'document' && item._payload.documentAssetId) {
        navigate(`/portfolio/${item._payload.documentAssetId}`);
      }
    });

    return () => {
      network.destroy();
      networkRef.current = null;
    };
  }, [filtered, navigate]);

  if (state.kind === 'loading') {
    return (
      <div className="page">
        <div className="page-subtitle">Loading entity graph…</div>
      </div>
    );
  }
  if (state.kind === 'error') {
    return (
      <div className="page">
        <div className="banner banner-error">{state.message}</div>
      </div>
    );
  }

  const counts: Record<string, number> = {};
  state.graph.nodes.forEach((n) => {
    counts[n.type] = (counts[n.type] ?? 0) + 1;
  });

  return (
    <div className="page entity-graph-page">
      <div className="crumb">
        <span>PracticeX</span>
        <span>›</span>
        <span>Entity graph</span>
      </div>
      <header className="page-head">
        <div>
          <div className="eyebrow">
            <span className="eyebrow-dot" />
            Cross-document network · {state.graph.nodes.length} nodes · {state.graph.links.length} edges
          </div>
          <h1 className="page-title">Entity graph</h1>
          <div className="page-subtitle">
            People, organizations, premises, and the documents that link them. Click a node to inspect.
            Double-click a document to drill in. Dashed edges are inferred co-appearance — same
            document, no direct relationship.
          </div>
        </div>
      </header>

      <section style={{ display: 'flex', gap: 10, marginBottom: 14, flexWrap: 'wrap' }}>
        {(['person', 'organization', 'asset', 'document'] as const).map((t) => {
          const isOn = filterTypes.has(t);
          const c = TYPE_COLORS[t];
          return (
            <button
              key={t}
              type="button"
              onClick={() => {
                setFilterTypes((prev) => {
                  const next = new Set(prev);
                  if (next.has(t)) next.delete(t);
                  else next.add(t);
                  logEvent('graph_chip_toggle', { type: t, on: next.has(t) });
                  return next;
                });
              }}
              className="graph-legend-chip"
              style={{
                opacity: isOn ? 1 : 0.4,
                borderColor: c.border,
                background: isOn ? c.background : 'transparent',
              }}
            >
              <span
                className="graph-legend-swatch"
                style={{ background: c.background, borderColor: c.border }}
              />
              <strong>{TYPE_LABELS[t]}</strong>
              <span className="muted">({counts[t] ?? 0})</span>
            </button>
          );
        })}
      </section>

      <div className="graph-layout">
        <div className="graph-canvas-wrapper">
          <div ref={containerRef} className="graph-canvas" />
        </div>
        <Card title="Selected" className="graph-side">
          {!selected ? (
            <div className="muted" style={{ fontSize: 13, lineHeight: 1.55 }}>
              Click a node to see details. The graph is force-directed: drag a node to reposition,
              scroll to zoom. Toggle the chips above to hide categories.
            </div>
          ) : (
            <SelectedNodePanel node={selected} graph={state.graph} />
          )}
        </Card>
      </div>
    </div>
  );
}

function SelectedNodePanel({ node, graph }: { node: EntityGraphNode; graph: EntityGraph }) {
  const incidentLinks = graph.links.filter((l) => l.source === node.id || l.target === node.id);
  const counterparts: { node: EntityGraphNode; link: EntityGraphLink }[] = [];
  for (const l of incidentLinks) {
    const otherId = l.source === node.id ? l.target : l.source;
    const other = graph.nodes.find((n) => n.id === otherId);
    if (other) counterparts.push({ node: other, link: l });
  }

  return (
    <div>
      <div className="eyebrow" style={{ fontSize: 11 }}>
        {TYPE_LABELS[node.type] ?? node.type}
      </div>
      <div style={{ fontSize: 16, fontWeight: 600, lineHeight: 1.35, margin: '4px 0 12px', wordBreak: 'break-word' }}>
        {node.label}
      </div>

      {node.type === 'document' && node.documentAssetId ? (
        <Link to={`/portfolio/${node.documentAssetId}`} className="px-button" style={{ display: 'inline-block', marginBottom: 14, padding: '6px 12px', textDecoration: 'none', fontSize: 12 }}>
          Open document
        </Link>
      ) : null}

      <div className="eyebrow" style={{ fontSize: 11, marginBottom: 6 }}>
        Connected · {counterparts.length}
      </div>
      <div className="graph-counterparts">
        {counterparts.slice(0, 30).map(({ node: other, link }, i) => (
          <div key={i} className="graph-counterpart-row">
            <span className="graph-counterpart-rel">{link.relation}</span>
            <span className="graph-counterpart-label" title={other.label}>
              {truncate(other.label, 50)}
            </span>
            {other.type === 'document' && other.documentAssetId ? (
              <Link to={`/portfolio/${other.documentAssetId}`} className="muted" style={{ fontSize: 11 }}>
                open
              </Link>
            ) : null}
          </div>
        ))}
        {counterparts.length > 30 ? (
          <div className="muted" style={{ fontSize: 11, marginTop: 6 }}>
            +{counterparts.length - 30} more…
          </div>
        ) : null}
      </div>
    </div>
  );
}

function truncate(s: string, n: number) {
  return s.length <= n ? s : s.slice(0, n - 1) + '…';
}

// Blend a hex/named CSS color toward white. Used to render dimmed nodes
// (filtered-out types) without removing them, so the bridge structure
// stays visible as faint context.
function dim(color: string, mix: number): string {
  const hex = color.startsWith('#') ? color.slice(1) : color;
  if (hex.length !== 6) return color;
  const r = parseInt(hex.slice(0, 2), 16);
  const g = parseInt(hex.slice(2, 4), 16);
  const b = parseInt(hex.slice(4, 6), 16);
  const m = Math.max(0, Math.min(1, mix));
  const blend = (v: number) => Math.round(v + (255 - v) * m);
  const toHex = (v: number) => v.toString(16).padStart(2, '0');
  return `#${toHex(blend(r))}${toHex(blend(g))}${toHex(blend(b))}`;
}
