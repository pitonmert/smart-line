import ProductionAndProducts from '../components/charts/ProductionAndProducts'
import { ErrorNote, Panel, StatusPill } from '../components/inspection/ui'
import { api } from '../api/client'
import { usePolling } from '../hooks/usePolling'

export default function AnalyticsPage() {
  const room = usePolling(() => api.getControlRoom(), 5000)
  return (
    <div className="flex flex-col gap-4">
      {room.error && <ErrorNote>{room.error}</ErrorNote>}
      <p className="text-xs text-text-muted">Real production only. Simulation is excluded.</p>
      <div className="grid grid-cols-2 gap-3 md:grid-cols-5">
        <StatusPill label="Inspections" value={String(room.data?.totalCount ?? 0)} />
        <StatusPill label="Pass" value={String(room.data?.acceptedCount ?? 0)} tone="good" />
        <StatusPill label="Reject" value={String(room.data?.rejectedCount ?? 0)} tone="bad" />
        <StatusPill label="Manual" value={String(room.data?.manualReviewCount ?? 0)} tone="warn" />
        <StatusPill label="Yield" value={room.data?.yieldPercent == null ? '—' : `${room.data.yieldPercent}%`} />
      </div>
      <Panel title="Inspection destinations">
        <ul className="grid grid-cols-2 gap-2 text-sm md:grid-cols-4">
          {(room.data?.binCounts ?? []).map(bin => <li key={bin.destinationBin} className="rounded-lg border border-card-border px-3 py-2">{bin.destinationBin}<div className="font-mono text-lg">{bin.count}</div></li>)}
        </ul>
        <p className="mt-3 text-xs text-text-muted">Yield is pass divided by pass plus reject. Manual review is outside that denominator. An empty decided population is shown as a dash.</p>
      </Panel>
      <Panel title="Barcode production counter">
        <ProductionAndProducts />
        <p className="mt-2 text-xs text-text-muted">This chart is the existing barcode counter. It is separate from recipe inspection yield.</p>
      </Panel>
    </div>
  )
}
