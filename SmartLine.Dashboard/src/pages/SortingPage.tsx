import { useState } from 'react'
import { api, getApiErrorMessage } from '../api/client'
import { useAuth } from '../auth/AuthContext'
import SimulationPanel from '../components/inspection/SimulationPanel'
import { formatTime } from '../components/inspection/format'
import { ErrorNote, Panel, SimulationBanner } from '../components/inspection/ui'
import { usePolling } from '../hooks/usePolling'

export default function SortingPage() {
  const { hasMinimumRole } = useAuth()
  const queue = usePolling(() => api.getSortQueue(), 3000)
  const diagnostics = usePolling(() => api.getDiagnostics(), 5000)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)

  const release = async (productId: string, code: string) => {
    if (!confirm(`Record a logical sort for ${code}? No servo command will be sent.`)) return
    setError(null)
    try {
      const result = await api.releaseTrack(productId)
      setMessage(`${result.track.publicCode} is ${result.track.stage}. ${result.actuation.message}`)
      queue.reload()
    } catch (reason) {
      setError(getApiErrorMessage(reason, 'Sort was not recorded.'))
    }
  }

  return (
    <div className="flex flex-col gap-4">
      {queue.data?.some(item => item.simulation) && <SimulationBanner text="One or more queued products are simulated." />}
      <Panel title="Actuation">
        <p className="text-sm">Hardware actuation: {diagnostics.data?.hardwareActuationEnabled ? 'enabled in configuration' : 'disabled by default'}</p>
        <p className="mt-2 text-xs text-text-muted">Logical destinations stay separate from servo motion. The release action records SORTED and does not publish a motion command.</p>
      </Panel>
      <Panel title="Sorting queue">
        <div className="overflow-x-auto">
          <table className="w-full text-left text-sm">
            <thead className="text-text-muted"><tr><th className="py-2">Product</th><th>Decision</th><th>Destination</th><th>Due</th><th></th></tr></thead>
            <tbody>
              {(queue.data ?? []).map(item => (
                <tr key={item.productId} className="border-t border-card-border">
                  <td className="py-2">{item.publicCode} {item.simulation && <span className="text-warning-light">SIMULATION</span>}</td>
                  <td>{item.decision}</td>
                  <td>{item.destinationBin}</td>
                  <td>{formatTime(item.sortDueAt)}</td>
                  <td>{hasMinimumRole('Operator') && <button type="button" className="rounded bg-primary px-2 py-1 text-xs text-white" onClick={() => release(item.productId, item.publicCode)}>Release</button>}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {queue.data && queue.data.length === 0 && <p className="text-sm text-text-muted">The queue is empty.</p>}
        </div>
        {message && <p className="mt-3 text-sm">{message}</p>}
        {error && <div className="mt-3"><ErrorNote>{error}</ErrorNote></div>}
      </Panel>
      <SimulationPanel onChanged={queue.reload} />
    </div>
  )
}
