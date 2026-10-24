import EnvironmentPanel from '../components/gauges/EnvironmentPanel'
import { Panel, StatusPill } from '../components/inspection/ui'
import { useDashboardState } from '../context/DashboardContext'
import { api } from '../api/client'
import { usePolling } from '../hooks/usePolling'

export default function SensorsPage() {
  const { conveyor } = useDashboardState()
  const diagnostics = usePolling(() => api.getDiagnostics(), 5000)
  return (
    <div className="flex flex-col gap-4">
      <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
        <StatusPill label="Temperature" value={conveyor ? `${conveyor.temperature} °C` : '…'} />
        <StatusPill label="Humidity" value={conveyor ? `${conveyor.humidity} %` : '…'} />
        <StatusPill label="Fan" value={conveyor?.fanActive ? 'On' : 'Off'} />
        <StatusPill label="Fire" value={conveyor?.isFireDetected ? 'Detected' : 'Clear'} tone={conveyor?.isFireDetected ? 'bad' : 'good'} />
      </div>
      <EnvironmentPanel />
      <Panel title="Time of flight">
        <p className="text-sm">Source: {diagnostics.data?.timeOfFlightSource ?? 'NOT_INTEGRATED'}</p>
        <p className="mt-2 text-sm text-text-muted">VL53L1X is not integrated. productPresent, distanceMm, triggerTimestamp, and sensorHealthy are not read from hardware. No distance is displayed as a measurement.</p>
      </Panel>
    </div>
  )
}
