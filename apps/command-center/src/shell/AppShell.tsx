import { useEffect, useState } from 'react';
import {
  Bell,
  Brain,
  Building2,
  Clock,
  FileStack,
  Gavel,
  Home,
  LogOut,
  Network,
  Search,
  Settings,
  ShieldAlert,
  Sparkles,
  Upload,
  Zap,
} from 'lucide-react';
import { NavLink, Outlet, useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import {
  analysisApi,
  getTenantOverride,
  setTenantOverride,
  type CurrentUser,
  type DashboardStats,
  type Facility,
  type TenantSummary,
} from '../lib/api';
import { logEvent, logPageView } from '../lib/analytics';

type WorkspaceItem = {
  to: string;
  label: string;
  icon: typeof Home;
  count?: number;
};

function BrandMark() {
  // Two crossing diagonal bars forming an "X" — orange `\` (top-left to
  // bottom-right) + green `/` (top-right to bottom-left). SVG keeps the
  // crossing crisp at small sizes where CSS rotations would alias.
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
      <path d="M 4.5 4 L 12 4 L 27.5 28 L 20 28 Z" fill="var(--px-orange, #d4631e)" />
      <path d="M 20 4 L 27.5 4 L 12 28 L 4.5 28 Z" fill="var(--px-green, #1d6f42)" />
    </svg>
  );
}

export function AppShell() {
  const [user, setUser] = useState<CurrentUser | null>(null);
  const [stats, setStats] = useState<DashboardStats | null>(null);
  const [facilities, setFacilities] = useState<Facility[]>([]);
  const [tenants, setTenants] = useState<TenantSummary[]>([]);
  const [shellLoaded, setShellLoaded] = useState(false);
  const navigate = useNavigate();
  const location = useLocation();
  const [searchParams] = useSearchParams();
  const activeFacility = searchParams.get('facility');

  // Fire a page_view event on every route change so post-demo analytics
  // can show which sections the guest explored. Runs on mount + on path
  // change; ignores trailing slashes.
  useEffect(() => {
    logPageView();
  }, [location.pathname, location.search]);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const [u, s, f, t] = await Promise.all([
          analysisApi.getCurrentUser().catch(() => null),
          analysisApi.getDashboard().catch(() => null),
          analysisApi.getFacilities().catch(() => []),
          analysisApi.getAccessibleTenants().catch(() => []),
        ]);
        if (cancelled) return;
        if (u) setUser(u);
        if (s) setStats(s);
        if (f) setFacilities(f);
        if (t) setTenants(t);
      } catch {
        // Sidebar gracefully degrades when API isn't reachable yet.
      } finally {
        if (!cancelled) setShellLoaded(true);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  // Slice 21 Phase 2: super-admin org-switcher.
  const onTenantSwitch = (tenantId: string) => {
    setTenantOverride(tenantId === '' ? null : tenantId);
    logEvent('tenant_switch', { tenantId });
    // Hard reload so every component re-pulls under the new tenant.
    window.location.reload();
  };
  const currentOverride = getTenantOverride();

  const workspaceItems: WorkspaceItem[] = [
    { to: '/dashboard', label: 'Command center', icon: Home },
    { to: '/portfolio', label: 'Portfolio intel', icon: Brain, count: stats?.documents },
    { to: '/contracts', label: 'Contracts', icon: FileStack, count: stats?.contractsTracked },
    { to: '/renewals', label: 'Renewals', icon: Clock },
    { to: '/legal-advisor', label: 'Legal Advisor', icon: Gavel },
    { to: '/graph', label: 'Entity graph', icon: Network },
    { to: '/alerts', label: 'Alerts', icon: ShieldAlert },
    { to: '/obligations', label: 'Obligations', icon: Sparkles },
    { to: '/review', label: 'Review queue', icon: Sparkles, count: stats?.reviewQueueDepth },
    { to: '/sources', label: 'Source discovery', icon: Upload },
  ];

  const userName = user?.name ?? '—';
  const userInitials = user?.initials ?? '?';
  // Once the initial load resolves we know whether the API answered. If it
  // didn't, drop the perpetual "Loading…" placeholder for a calm,
  // controlled label so the sidebar doesn't read as broken during a
  // workspace-down window.
  const tenantName = user?.tenantName ?? (shellLoaded ? 'Workspace' : 'Loading…');

  // Cloudflare Access intercepts /cdn-cgi/access/logout on any
  // Access-protected hostname and clears the application session
  // cookie. After redirect the user lands back on the OTP gate.
  // In local dev (no Access in front) this just 404s — harmless.
  const handleLogout = () => {
    logEvent('logout_clicked', { email: user?.email ?? null });
    window.location.href = '/cdn-cgi/access/logout';
  };

  return (
    <div className="app" data-theme="operator" data-density="comfortable">
      <header className="topbar">
        <div className="brand">
          <BrandMark />
          <span className="brand-name">PracticeX Command Center</span>
        </div>
        {/* Slice 21 Phase 2: org switcher (super-admin only). */}
        {user?.isSuperAdmin && tenants.length > 1 ? (
          <select
            value={currentOverride ?? ''}
            onChange={(e) => onTenantSwitch(e.target.value)}
            title="Switch organization context (super-admin)"
            style={{
              fontSize: 12,
              padding: '4px 8px',
              marginRight: 12,
              border: '1px solid var(--px-border, #d4d4d4)',
              borderRadius: 6,
              background: 'rgba(212,99,30,0.05)',
              color: 'var(--px-orange, #d4631e)',
              fontWeight: 600,
              cursor: 'pointer',
            }}
          >
            <option value="">— Platform (default) —</option>
            {tenants.map((t) => (
              <option key={t.id} value={t.id}>
                {t.name}
              </option>
            ))}
          </select>
        ) : null}
        <button
          className="facility-switch"
          type="button"
          onClick={() => navigate('/portfolio')}
          title={activeFacility ? 'Click to clear facility filter' : 'All facilities'}
        >
          <Building2 size={13} />
          <span className="mono-label">Facility</span>
          <strong>
            {(() => {
              if (activeFacility) {
                const f = facilities.find((x) => x.id === activeFacility);
                return f ? f.name : 'Filtered';
              }
              if (facilities.length === 0) return 'All facilities';
              if (facilities.length === 1) return facilities[0].name;
              return `${facilities.length} facilities`;
            })()}
          </strong>
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
        <button
          className="px-icon-button"
          type="button"
          aria-label="Sign out"
          title={user?.email ? `Sign out (${user.email})` : 'Sign out'}
          onClick={handleLogout}
        >
          <LogOut size={15} />
        </button>
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
              <button
                className={`facility-pill ${!activeFacility ? 'active' : ''}`}
                type="button"
                onClick={() => {
                  logEvent('facility_filter', { facilityId: 'all' });
                  navigate('/portfolio');
                }}
              >
                <span className="facility-code">ALL</span>
                <span>Portfolio view</span>
                <span className="nav-count">
                  {facilities.reduce((sum, f) => sum + (f.documentCount ?? 0), 0)}
                </span>
              </button>
              {facilities.map((facility) => (
                <button
                  className={`facility-pill ${activeFacility === facility.id ? 'active' : ''}`}
                  key={facility.id}
                  type="button"
                  onClick={() => {
                    logEvent('facility_filter', {
                      facilityId: facility.id,
                      facilityName: facility.name,
                    });
                    navigate(`/portfolio?facility=${encodeURIComponent(facility.id)}`);
                  }}
                >
                  <span className="facility-code">{facility.code}</span>
                  <span>{facility.name}</span>
                  {facility.documentCount > 0 ? (
                    <span className="nav-count">{facility.documentCount}</span>
                  ) : null}
                </button>
              ))}
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
          {/* Slice 21 RBAC: surface role + scope so the user knows what
              they're looking at. Backend is what enforces — this is just
              a visible affordance, not a guard. */}
          {user ? (
            <div style={{ marginTop: 6, fontSize: 11, lineHeight: 1.55 }}>
              <span
                style={{
                  display: 'inline-block',
                  padding: '2px 8px',
                  borderRadius: 999,
                  background:
                    user.role === 'super_admin' ? 'rgba(212,99,30,0.15)'
                    : user.role === 'org_admin' ? 'rgba(29,111,66,0.15)'
                    : 'rgba(0,0,0,0.06)',
                  color:
                    user.role === 'super_admin' ? 'var(--px-orange, #d4631e)'
                    : user.role === 'org_admin' ? 'var(--px-green, #1d6f42)'
                    : 'var(--px-ink, #555)',
                  fontWeight: 600,
                  letterSpacing: 0.4,
                  textTransform: 'uppercase',
                  fontSize: 10,
                }}
                title={
                  user.role === 'super_admin'
                    ? 'Super Admin · access to all organizations + facilities'
                    : user.role === 'org_admin'
                    ? 'Org Admin · all facilities in this organization'
                    : `Facility scope · ${(user.accessibleFacilityIds ?? []).length} facility/facilities`
                }
              >
                {user.role === 'super_admin' ? 'Super Admin'
                  : user.role === 'org_admin' ? 'Org Admin'
                  : 'Facility'}
              </span>
              {!user.isSuperAdmin && user.role === 'facility_user' && user.accessibleFacilityIds ? (
                <span className="muted" style={{ marginLeft: 8 }}>
                  scope: {user.accessibleFacilityIds.length} facility
                  {user.accessibleFacilityIds.length === 1 ? '' : 'ies'}
                </span>
              ) : null}
            </div>
          ) : null}
          {stats ? (
            <div className="muted" style={{ marginTop: 4 }}>
              {stats.documents} docs · {stats.totalSizeMb.toFixed(1)} MB processed
            </div>
          ) : shellLoaded ? (
            <div className="muted" style={{ marginTop: 4 }}>
              Stay tuned — good things coming.
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
