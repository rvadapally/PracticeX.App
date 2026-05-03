import { useEffect, useState } from 'react';
import {
  Bell,
  Brain,
  Building2,
  Clock,
  FileStack,
  Home,
  Network,
  Search,
  Settings,
  ShieldAlert,
  Sparkles,
  Upload,
  Zap,
} from 'lucide-react';
import { NavLink, Outlet } from 'react-router-dom';
import { analysisApi, type CurrentUser, type DashboardStats, type Facility } from '../lib/api';

type WorkspaceItem = {
  to: string;
  label: string;
  icon: typeof Home;
  count?: number;
};

function BrandMark() {
  // Two interlocking chevrons forming a "V" mark - orange (left) + green (right).
  // SVG is precise across browsers in a way CSS pseudo-element rotations
  // cannot match, especially at small sizes.
  return (
    <svg
      className="brand-mark"
      viewBox="0 0 32 32"
      width="30"
      height="30"
      aria-hidden="true"
      role="img"
    >
      <title>PracticeX</title>
      <path d="M 4.5 4 L 12 4 L 18 28 L 10.5 28 Z" fill="var(--px-orange, #d4631e)" />
      <path d="M 20 4 L 27.5 4 L 21.5 28 L 14 28 Z" fill="var(--px-green, #1d6f42)" />
    </svg>
  );
}

export function AppShell() {
  const [user, setUser] = useState<CurrentUser | null>(null);
  const [stats, setStats] = useState<DashboardStats | null>(null);
  const [facilities, setFacilities] = useState<Facility[]>([]);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const [u, s, f] = await Promise.all([
          analysisApi.getCurrentUser().catch(() => null),
          analysisApi.getDashboard().catch(() => null),
          analysisApi.getFacilities().catch(() => []),
        ]);
        if (cancelled) return;
        if (u) setUser(u);
        if (s) setStats(s);
        if (f) setFacilities(f);
      } catch {
        // Sidebar gracefully degrades when API isn't reachable yet.
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  const workspaceItems: WorkspaceItem[] = [
    { to: '/dashboard', label: 'Command center', icon: Home },
    { to: '/portfolio', label: 'Portfolio intel', icon: Brain, count: stats?.documents },
    { to: '/contracts', label: 'Contracts', icon: FileStack, count: stats?.contractsTracked },
    { to: '/renewals', label: 'Renewals', icon: Clock },
    { to: '/graph', label: 'Entity graph', icon: Network },
    { to: '/alerts', label: 'Alerts', icon: ShieldAlert },
    { to: '/obligations', label: 'Obligations', icon: Sparkles },
    { to: '/review', label: 'Review queue', icon: Sparkles, count: stats?.reviewQueueDepth },
    { to: '/sources', label: 'Source discovery', icon: Upload },
  ];

  const userName = user?.name ?? '—';
  const userInitials = user?.initials ?? '?';
  const tenantName = user?.tenantName ?? 'Loading…';

  return (
    <div className="app" data-theme="operator" data-density="comfortable">
      <header className="topbar">
        <div className="brand">
          <BrandMark />
          <span className="brand-name">PracticeX Command Center</span>
        </div>
        <button className="facility-switch" type="button">
          <Building2 size={13} />
          <span className="mono-label">Facility</span>
          <strong>{facilities.length === 0 ? 'All facilities' : `${facilities.length} facilities`}</strong>
        </button>
        <div className="topbar-spacer" />
        <div className="cmdk" role="search">
          <Search size={13} />
          <span>Search contracts, counterparties, facilities…</span>
          <kbd>⌘K</kbd>
        </div>
        <button className="px-icon-button" type="button" aria-label="Notifications">
          <Bell size={15} />
        </button>
        <button className="px-icon-button" type="button" aria-label="Settings">
          <Settings size={15} />
        </button>
        <div className="px-avatar" title={userName}>{userInitials}</div>
      </header>
      <aside className="sidebar">
        <section className="nav-section">
          <h2 className="section-label">Workspace</h2>
          {workspaceItems.map((item) => (
            <NavLink className={({ isActive }) => `nav-item ${isActive ? 'active' : ''}`} key={item.to} to={item.to}>
              <item.icon size={14} />
              <span>{item.label}</span>
              {typeof item.count === 'number' && item.count > 0 ? (
                <span className="nav-count">{item.count}</span>
              ) : null}
            </NavLink>
          ))}
        </section>
        {facilities.length > 0 ? (
          <section className="nav-section">
            <h2 className="section-label">Facilities</h2>
            <div className="facility-list">
              {facilities.map((facility) => (
                <button className="facility-pill" key={facility.id} type="button">
                  <span className="facility-code">{facility.code}</span>
                  <span>{facility.name}</span>
                </button>
              ))}
              <button className="facility-pill active" type="button">
                <span className="facility-code">ALL</span>
                <span>Portfolio view</span>
              </button>
            </div>
          </section>
        ) : null}
        <section className="nav-section">
          <h2 className="section-label">Intelligence · locked</h2>
          <NavLink className="nav-item" to="/rates">
            <Zap size={14} />
            <span>Rate visibility</span>
            <span className="nav-count">PRO</span>
          </NavLink>
        </section>
        <div style={{ flex: 1 }} />
        <div className="plan-card">
          <div className="section-label" style={{ marginLeft: 0 }}>Tenant</div>
          <strong>{tenantName}</strong>
          {stats ? (
            <div className="muted" style={{ marginTop: 4 }}>
              {stats.documents} docs · {stats.totalSizeMb.toFixed(1)} MB processed
            </div>
          ) : null}
        </div>
      </aside>
      <main className="main">
        <Outlet />
      </main>
    </div>
  );
}
