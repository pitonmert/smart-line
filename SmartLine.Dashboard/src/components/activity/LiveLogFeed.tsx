import { FaListAlt } from 'react-icons/fa'
import { useDashboardState } from '../../context/DashboardContext'

const severityStyles: Record<string, string> = {
  ERROR: 'border-danger/30 bg-danger/10 text-danger-light',
  WARNING: 'border-warning/30 bg-warning/10 text-warning-light',
  INFO: 'border-primary/20 bg-primary/5 text-primary-light',
}

export default function LiveLogFeed() {
  const { recentLogs } = useDashboardState()

  return (
    <section className="rounded-xl border border-card-border bg-card p-5">
      <div className="mb-4 flex items-center gap-2">
        <FaListAlt className="text-primary-light" />
        <h2 className="text-base font-semibold">Canlı Sistem Akışı</h2>
      </div>

      {recentLogs.length === 0 ? (
        <p className="text-sm text-text-muted">Yeni sistem olayı bekleniyor.</p>
      ) : (
        <div className="grid gap-2 md:grid-cols-2">
          {recentLogs.map(log => (
            <article
              key={log.id}
              className={`flex items-start justify-between gap-3 rounded-lg border px-3 py-2 text-xs ${severityStyles[log.severity] ?? 'border-card-border bg-surface text-text'}`}
            >
              <div className="min-w-0">
                <div className="font-semibold">{log.eventType}</div>
                {log.message && <p className="mt-0.5 truncate text-text-muted">{log.message}</p>}
              </div>
              <time className="shrink-0 font-mono text-text-muted">
                {new Date(log.createdAt).toLocaleTimeString('tr-TR')}
              </time>
            </article>
          ))}
        </div>
      )}
    </section>
  )
}
