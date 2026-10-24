import { api } from '../api/client'
import LiveLogFeed from '../components/activity/LiveLogFeed'
import { formatTime, healthTone } from '../components/inspection/format'
import { Panel, StatusPill } from '../components/inspection/ui'
import { useDashboardState } from '../context/DashboardContext'
import { usePolling } from '../hooks/usePolling'

export default function DiagnosticsPage() {
  const report = usePolling(() => api.getDiagnostics(), 4000)
  const { connection } = useDashboardState()
  const data = report.data
  const model = data?.model
  const frameSize = data?.frameWidth && data?.frameHeight ? `${data.frameWidth}×${data.frameHeight}` : 'No frame reported'
  const rows: [string, string][] = data ? [
    ['API', data.api],
    ['Database', data.database],
    ['MQTT', data.mqtt],
    ['Vision', data.vision],
    ['Camera', data.camera],
    ['Camera host', data.cameraStreamHost ?? 'Not reported'],
    ['Last frame', formatTime(data.lastFrameAt)],
    ['Frame size', frameSize],
    ['Configured still', data.configuredStillWidth && data.configuredStillHeight ? `${data.configuredStillWidth}×${data.configuredStillHeight}` : 'Not reported'],
    ['Last still', data.lastStillWidth && data.lastStillHeight ? `${data.lastStillWidth}×${data.lastStillHeight}` : 'Not reported'],
    ['Last still error', data.lastStillError ?? 'Not reported'],
    ['Preview fallback', data.previewFallbackAllowed == null ? 'Not reported' : String(data.previewFallbackAllowed)],
    ['Sensor PID', data.sensorPid ?? 'Not reported'],
    ['Camera failures', data.cameraConsecutiveFailures == null ? 'Not reported' : String(data.cameraConsecutiveFailures)],
    ['Last inspection', formatTime(data.lastInspectionAt)],
    ['Model', `${data.modelStatus} ${data.modelName ?? ''} ${data.modelVersion ?? ''}`.trim()],
    ['Model configured', model ? String(model.configured) : 'Not reported'],
    ['Model loaded', model ? String(model.loaded) : 'Not reported'],
    ['Model backend', model?.backend ?? 'Not reported'],
    ['Model task', model?.task ?? 'Not reported'],
    ['Model artifact', model?.artifact ?? 'Not registered'],
    ['Last inference', model?.lastInferenceLatencyMs == null ? 'Not measured' : `${model.lastInferenceLatencyMs} ms`],
    ['Model load error', model?.loadError ?? 'None'],
    ['Confidence threshold', model?.confidenceThreshold == null ? 'Not reported' : String(model.confidenceThreshold)],
    ['ESP32', data.esp32],
    ['SignalR hub', data.signalR],
    ['SignalR client', connection],
    ['Simulation enabled', String(data.simulation.enabled)],
    ['Simulation active', String(data.simulation.active)],
    ['Simulation latch', String(data.simulation.latched)],
    ['In-flight simulated products', String(data.simulation.inFlightSimulatedProducts)],
    ['Hardware actuation', String(data.hardwareActuationEnabled)],
    ['Environment', data.environment],
    ['Active recipe', data.activeRecipe ?? 'None'],
    ['Active recipe version', data.activeRecipeVersion == null ? 'Not reported' : String(data.activeRecipeVersion)],
    ['Time of flight', data.timeOfFlightSource],
    ['Camera stream', data.cameraStreamUrl ?? 'Not configured'],
  ] : []

  return (
    <div className="flex flex-col gap-4">
      <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
        <StatusPill label="API" value={data?.api ?? '…'} tone={healthTone(data?.api)} />
        <StatusPill label="Database" value={data?.database ?? '…'} tone={healthTone(data?.database)} />
        <StatusPill label="MQTT" value={data?.mqtt ?? '…'} tone={healthTone(data?.mqtt)} />
        <StatusPill label="Vision" value={data?.vision ?? '…'} tone={healthTone(data?.vision)} />
        <StatusPill label="Camera" value={data?.camera ?? '…'} tone={healthTone(data?.camera)} />
        <StatusPill label="Model" value={data?.modelStatus ?? '…'} tone={healthTone(data?.modelStatus)} />
        <StatusPill label="Backend" value={model?.backend ?? 'Not reported'} tone={model?.loaded ? 'good' : 'warn'} />
        <StatusPill label="SignalR" value={connection} tone={healthTone(connection)} />
      </div>
      <Panel title="Configuration">
        <dl className="grid grid-cols-1 gap-2 text-sm md:grid-cols-2">
          {rows.map(([label, value]) => (
            <div key={label} className="flex justify-between gap-4 border-b border-card-border py-1"><dt className="text-text-muted">{label}</dt><dd className="text-right">{value}</dd></div>
          ))}
        </dl>
        <p className="mt-3 text-xs text-text-muted">Secrets, passwords, connection strings, and certificates are not part of this response. Vision is ONLINE only after a health response, OFFLINE when that request fails, and NOT_CONFIGURED when no health URL is set. Camera stays UNKNOWN until Vision is ONLINE. A model is loaded only when Vision reports that the artifact loaded. Latency is omitted until an inspection actually runs.</p>
        {report.error && <p className="mt-3 text-sm text-danger-light">{report.error}</p>}
      </Panel>
      <LiveLogFeed />
    </div>
  )
}
