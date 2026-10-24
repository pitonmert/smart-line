import { useState, type FormEvent } from 'react'
import { api } from '../api/client'
import { formatTime } from '../components/inspection/format'
import { ErrorNote, Panel } from '../components/inspection/ui'
import { usePolling } from '../hooks/usePolling'

export default function HistoryPage() {
  const [query, setQuery] = useState('')
  const [page, setPage] = useState(1)
  const history = usePolling(
    () => api.getInspections(`${query ? `${query}&` : '?'}pageNumber=${page}&pageSize=20`),
    5000,
    [query, page],
  )

  const search = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    const form = new FormData(event.currentTarget)
    const params = new URLSearchParams()
    for (const key of ['decision', 'productClass', 'q']) {
      const value = String(form.get(key) || '').trim()
      if (value) params.set(key, value)
    }
    if (form.get('simulation') === 'true' || form.get('simulation') === 'false') params.set('simulation', String(form.get('simulation')))
    const text = params.toString()
    setPage(1)
    setQuery(text ? `?${text}` : '')
  }

  return (
    <Panel title="Inspection history" action={<span className="text-xs text-text-muted">Page {history.data?.pageNumber ?? page}</span>}>
      {history.error && <ErrorNote>{history.error}</ErrorNote>}
      <form className="mb-4 grid grid-cols-1 gap-2 md:grid-cols-5" onSubmit={search}>
        <input name="q" placeholder="Search class, bin, decision, product" className="rounded-lg border border-card-border bg-surface px-3 py-2 text-sm" />
        <input name="productClass" placeholder="Class" className="rounded-lg border border-card-border bg-surface px-3 py-2 text-sm" />
        <select name="decision" className="rounded-lg border border-card-border bg-surface px-3 py-2 text-sm"><option value="">Any decision</option><option>PASS</option><option>REJECT</option><option>MANUAL_REVIEW</option></select>
        <select name="simulation" className="rounded-lg border border-card-border bg-surface px-3 py-2 text-sm"><option value="">Any source</option><option value="true">SIMULATION</option><option value="false">Not simulation</option></select>
        <button className="rounded-lg bg-primary px-3 py-2 text-sm text-white" type="submit">Search</button>
      </form>
      <div className="overflow-x-auto">
        <table className="w-full min-w-[860px] text-left text-sm">
          <thead className="text-text-muted"><tr><th className="py-2">Time</th><th>Product class</th><th>Color</th><th>Size</th><th>Defects</th><th>Decision</th><th>Destination</th><th>Recipe</th><th>Model</th><th>Latency</th><th>Source</th></tr></thead>
          <tbody>
            {(history.data?.items ?? []).map(item => (
              <tr key={item.inspectionId} className="border-t border-card-border">
                <td className="py-2">{formatTime(item.processedAt)}</td>
                <td>{item.productClass}</td>
                <td>{item.colorClass}</td>
                <td>{item.sizeClass}</td>
                <td>{item.defectClasses.join(', ') || '—'}</td>
                <td>{item.decision}</td>
                <td>{item.destinationBin}</td>
                <td>{item.recipeVersion == null ? '—' : `v${item.recipeVersion}`}</td>
                <td>{item.modelVersion ?? '—'}</td>
                <td>{item.processingLatencyMs ?? '—'}</td>
                <td>{item.simulation ? <span className="font-semibold text-warning-light">SIMULATION</span> : 'Live observation'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <div className="mt-3 flex gap-2">
        <button type="button" disabled={page <= 1} className="rounded border border-card-border px-3 py-1 text-sm disabled:opacity-40" onClick={() => setPage(value => Math.max(1, value - 1))}>Previous</button>
        <button type="button" disabled={!history.data?.hasNextPage} className="rounded border border-card-border px-3 py-1 text-sm disabled:opacity-40" onClick={() => setPage(value => value + 1)}>Next</button>
      </div>
    </Panel>
  )
}
