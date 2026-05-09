import { ShieldAlert } from 'lucide-react';

/**
 * Slice 20 — required disclaimer banner for any Legal Advisor surface.
 *
 * The Counsel's Memo and Counsel's Brief are AI-generated and read like
 * an attorney's work product. The disclaimer is a non-negotiable framing
 * device that prevents the output from being construed as the practice
 * of law or the formation of an attorney-client relationship. Render this
 * **above** any memo/brief content; do not collapse it into a footer.
 */
export function LegalDisclaimer({
  variant = 'default',
}: {
  variant?: 'default' | 'compact';
}) {
  const compact = variant === 'compact';
  return (
    <div
      role="note"
      aria-label="Legal disclaimer"
      style={{
        display: 'flex',
        gap: 10,
        alignItems: 'flex-start',
        padding: compact ? '8px 12px' : '12px 14px',
        marginBottom: compact ? 8 : 16,
        background: 'rgba(212, 99, 30, 0.08)',
        border: '1px solid rgba(212, 99, 30, 0.35)',
        borderRadius: 8,
        fontSize: compact ? 11 : 12,
        lineHeight: 1.55,
        color: 'var(--px-ink, #1a1a1a)',
      }}
    >
      <ShieldAlert
        size={compact ? 14 : 16}
        style={{ color: 'var(--px-orange, #d4631e)', flexShrink: 0, marginTop: 1 }}
        aria-hidden="true"
      />
      <div>
        <strong style={{ color: 'var(--px-orange, #d4631e)' }}>
          Not legal advice.
        </strong>{' '}
        AI-generated analysis for informational purposes only. Does not
        establish an attorney-client relationship. Engage licensed counsel
        before relying on any conclusion or taking action based on this
        output.
      </div>
    </div>
  );
}
