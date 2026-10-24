import type { ReactNode } from 'react'

export function Panel({ title, action, children }: { title: string; action?: ReactNode; children: ReactNode }) {
  return (
    <section className="rounded-xl border border-card-border bg-card p-4">
      <div className="mb-3 flex items-start justify-between gap-3">
        <h2 className="text-sm font-semibold tracking-wide">{title}</h2>
        {action}
      </div>
      {children}
    </section>
  )
}

export function StatusPill({ label, value, tone = 'neutral' }: { label: string; value: string; tone?: 'neutral' | 'good' | 'bad' | 'warn' }) {
  const toneClass = {
    neutral: 'text-text',
    good: 'text-success-light',
    bad: 'text-danger-light',
    warn: 'text-warning-light',
  }[tone]
  return (
    <div className="rounded-lg border border-card-border bg-surface px-3 py-2">
      <div className="text-[11px] uppercase tracking-wide text-text-muted">{label}</div>
      <div className={`mt-1 truncate text-sm font-medium ${toneClass}`}>{value}</div>
    </div>
  )
}

export function SimulationBanner({ text }: { text: string }) {
  return (
    <div className="rounded-lg border border-warning/50 bg-warning/15 px-4 py-2 text-sm font-semibold tracking-wide text-warning-light">
      SIMULATION MODE — {text}
    </div>
  )
}

export function ErrorNote({ children }: { children: ReactNode }) {
  return <p className="rounded-lg border border-danger/40 bg-danger/10 px-3 py-2 text-sm text-danger-light">{children}</p>
}
