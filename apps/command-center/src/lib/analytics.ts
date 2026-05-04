/**
 * Lightweight client-side activity logger.
 *
 * Posts page-view + key-action events to /api/analytics/event. The server
 * tags each event with the authenticated email from Cloudflare Access's
 * Cf-Access-Authenticated-User-Email header. Used post-demo to see which
 * sections a guest spent time on.
 *
 * Fire-and-forget by design — never throws, never blocks rendering. A
 * dropped event is acceptable; a stuck UI is not.
 */

const API_BASE = (import.meta.env.VITE_API_BASE as string | undefined) ?? '/api';

type EventMetadata = Record<string, string | number | boolean | null>;

export function logEvent(eventType: string, metadata?: EventMetadata): void {
  try {
    const payload = {
      eventType,
      path: window.location.pathname + window.location.search,
      referrer: document.referrer || null,
      metadata: metadata ?? null,
    };
    // navigator.sendBeacon is the right primitive for fire-and-forget telemetry —
    // it survives navigation and doesn't block the request thread. Falls back
    // to fetch with keepalive on browsers without it.
    const url = `${API_BASE}/analytics/event`;
    const body = JSON.stringify(payload);
    if (typeof navigator !== 'undefined' && typeof navigator.sendBeacon === 'function') {
      const blob = new Blob([body], { type: 'application/json' });
      navigator.sendBeacon(url, blob);
      return;
    }
    void fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body,
      credentials: 'include',
      keepalive: true,
    }).catch(() => {
      // Telemetry failures are silent on purpose.
    });
  } catch {
    // Telemetry failures are silent on purpose.
  }
}

export function logPageView(): void {
  logEvent('page_view');
}
