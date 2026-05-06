import { useEffect, useState } from 'react';
import { Sparkles } from 'lucide-react';
import { Button } from '@practicex/design-system';

/**
 * Calm, controlled placeholder for whenever a top-level data load fails
 * (API down, tenant DB still bootstrapping, demo environment between
 * snapshots, etc.). Replaces the angry red "Failed to load…" banner that
 * leaks raw HTTP error detail to the viewer.
 *
 * The component is intentionally surface-area-minimal: a brand mark, a
 * one-line headline, a calm sub-line, and an optional Retry. No HTTP
 * status, no error-message strings, no stack traces.
 *
 * Visible to anyone who has already cleared Cloudflare Access OTP, so
 * we can speak in the first person ("we") without worrying about
 * marketing copy on a public surface.
 */
export interface MaintenanceMessageProps {
  /**
   * Optional override for the eyebrow. Defaults to a short section label
   * tied to the page so the layout still feels intentional.
   */
  eyebrow?: string;
  /**
   * Optional override for the main headline. Default is the calm
   * "good things coming" message Raghu approved for the demo posture.
   */
  headline?: string;
  /**
   * Optional override for the body copy. Default explains that the
   * workspace is being prepared and content will reappear shortly.
   */
  body?: string;
  /**
   * If provided, renders a Retry button that calls this handler. Hide
   * the button by leaving onRetry undefined.
   */
  onRetry?: () => void;
}

// Auto-retry schedule for transient outages (deploy/restart windows,
// mobile cellular cycles, Access cookie refresh). Backs off so we
// don't hammer a degraded backend, and stops after the third try so
// the user gets the manual button instead of an infinite spinner.
const AUTO_RETRY_DELAYS_MS = [3000, 6000, 12000];

export function MaintenanceMessage({
  eyebrow = 'PracticeX Command Center',
  headline = 'Stay tuned — good things coming.',
  body = "We're polishing this workspace right now. The portfolio, briefs, and intelligence views will be back here shortly.",
  onRetry,
}: MaintenanceMessageProps) {
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    if (!onRetry) return;
    if (attempt >= AUTO_RETRY_DELAYS_MS.length) return;
    const handle = window.setTimeout(() => {
      onRetry();
      setAttempt((n) => n + 1);
    }, AUTO_RETRY_DELAYS_MS[attempt]);
    return () => window.clearTimeout(handle);
  }, [attempt, onRetry]);

  const exhausted = attempt >= AUTO_RETRY_DELAYS_MS.length;

  return (
    <div className="maintenance-card" role="status" aria-live="polite">
      <div className="maintenance-icon" aria-hidden="true">
        <Sparkles size={20} />
      </div>
      <div className="maintenance-eyebrow">{eyebrow}</div>
      <h2 className="maintenance-headline">{headline}</h2>
      <p className="maintenance-body">{body}</p>
      {onRetry ? (
        <div className="maintenance-actions">
          <Button
            onClick={() => {
              onRetry();
              setAttempt(0);
            }}
            variant="secondary"
          >
            {exhausted ? 'Check again' : 'Reconnecting…'}
          </Button>
        </div>
      ) : null}
    </div>
  );
}

/**
 * Convenience wrapper for the most common usage: a full-page placeholder
 * inside the standard `.page` container, keeping the layout chrome
 * (sidebar / topbar) intact while the content area is unavailable.
 */
export function MaintenancePage(props: MaintenanceMessageProps) {
  return (
    <div className="page">
      <MaintenanceMessage {...props} />
    </div>
  );
}
