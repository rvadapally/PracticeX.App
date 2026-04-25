import type { ButtonHTMLAttributes, PropsWithChildren, ReactNode } from 'react';

type Tone = 'default' | 'ok' | 'warn' | 'accent';

export function Button({
  children,
  className = '',
  variant = 'primary',
  ...props
}: PropsWithChildren<ButtonHTMLAttributes<HTMLButtonElement> & { variant?: 'primary' | 'secondary' | 'confirm' }>) {
  return (
    <button className={`px-button ${variant} ${className}`.trim()} type="button" {...props}>
      {children}
    </button>
  );
}

export function Badge({ children }: PropsWithChildren) {
  return <span className="px-badge">{children}</span>;
}

export function StatusChip({ children, tone = 'default' }: PropsWithChildren<{ tone?: Tone }>) {
  return (
    <span className={`px-status-chip ${tone}`}>
      <span className="px-status-dot" />
      {children}
    </span>
  );
}

export function ConfidenceBar({ value, tone = 'ok' }: { value: number; tone?: Extract<Tone, 'ok' | 'warn' | 'accent'> }) {
  return (
    <span className="px-confidence-bar" aria-label={`Confidence ${value}%`}>
      <span className={tone} style={{ width: `${Math.max(0, Math.min(value, 100))}%` }} />
    </span>
  );
}

export function Card({ children, eyebrow, title }: PropsWithChildren<{ eyebrow?: string; title: string }>) {
  return (
    <section className="px-card">
      <header className="px-card-header">
        <h2>{title}</h2>
        {eyebrow ? <span>{eyebrow}</span> : null}
      </header>
      <div className="px-card-body">{children}</div>
    </section>
  );
}

export function KpiCard({ helper, label, tone = 'default', value }: { helper: string; label: string; tone?: Tone; value: ReactNode }) {
  return (
    <section className={`px-kpi-card ${tone}`}>
      <div className="mono-label">{label}</div>
      <strong>{value}</strong>
      <div className="muted">{helper}</div>
    </section>
  );
}

