import { useState } from 'react'
import { api, getApiErrorMessage } from '../../api/client'
import { usePolling } from '../../hooks/usePolling'
import { useAuth } from '../../auth/AuthContext'
import { fieldClass } from './format'
import { ErrorNote, Panel } from './ui'

const presets = [
  { name: 'Red large', productClass: 'BOX_RED', colorClass: 'RED', sizeClass: 'LARGE', defectClasses: [] as string[], confidence: 0.93 },
  { name: 'Blue medium', productClass: 'BOX_BLUE', colorClass: 'BLUE', sizeClass: 'MEDIUM', defectClasses: [] as string[], confidence: 0.9 },
  { name: 'Damaged', productClass: 'BOX_GREEN', colorClass: 'GREEN', sizeClass: 'SMALL', defectClasses: ['DAMAGED'], confidence: 0.88 },
]

export default function SimulationPanel({ onChanged }: { onChanged?: () => void }) {
  const { hasMinimumRole } = useAuth()
  const [message, setMessage] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const status = usePolling(() => api.getSimulationStatus(), 8000)
  const canOperate = hasMinimumRole('Operator') && status.data?.enabled === true

  const arrive = async (preset: (typeof presets)[number]) => {
    setBusy(true)
    setError(null)
    try {
      const result = await api.simulateArrival({
        ...preset,
        confidence: preset.confidence,
        distanceMm: 1000,
        beltSpeedMmPerSecond: 200,
        sourceWidth: 240,
        sourceHeight: 176,
      })
      setMessage(`${result.track.publicCode} ${result.inspection.decision} → ${result.inspection.destinationBin}. ${result.actuation.message}`)
      onChanged?.()
    } catch (reason) {
      setError(getApiErrorMessage(reason, 'Simulation arrival failed.'))
    } finally {
      setBusy(false)
    }
  }

  const tick = async () => {
    setBusy(true)
    setError(null)
    try {
      const result = await api.tickSimulation(new Date(Date.now() + 60_000).toISOString())
      setMessage(`SIMULATION tick sorted ${result.sortedCount}. ${result.actuation.message}`)
      onChanged?.()
    } catch (reason) {
      setError(getApiErrorMessage(reason, 'Simulation tick failed.'))
    } finally {
      setBusy(false)
    }
  }

  if (!canOperate) return null

  return (
    <Panel title="Inspection simulator">
      <p className="mb-3 text-xs text-warning-light">SIMULATION observations only. These values are not sensor or model measurements. No actuator command is sent.</p>
      <div className="flex flex-wrap gap-2">
        {presets.map(preset => (
          <button key={preset.name} type="button" disabled={busy} onClick={() => arrive(preset)} className={`${fieldClass} w-auto px-3 disabled:opacity-50`}>
            {preset.name}
          </button>
        ))}
        <button type="button" disabled={busy} onClick={tick} className={`${fieldClass} w-auto px-3 disabled:opacity-50`}>Advance due queue</button>
      </div>
      {message && <p className="mt-3 text-sm text-text-muted">{message}</p>}
      {error && <div className="mt-3"><ErrorNote>{error}</ErrorNote></div>}
    </Panel>
  )
}
