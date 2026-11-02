import { usePlant } from '../context/PlantContext'
import { useLanguage } from '../i18n/LanguageContext'
import LiveLogFeed from '../components/activity/LiveLogFeed'
import { healthTone } from '../components/inspection/format'
import { Panel, StatusPill } from '../components/inspection/ui'
import { useDashboardState } from '../context/DashboardContext'

export default function DiagnosticsPage() {
  const { diagnostics: report, room } = usePlant()
  const { date, number, t, text } = useLanguage()
  const { connection } = useDashboardState()
  const data = report.data
  const model = data?.model
  const frameSize = data?.frameWidth && data?.frameHeight ? `${data.frameWidth}×${data.frameHeight}` : t('No frame reported')
  const rows: [string, string][] = data ? [
    ['API', data.api],
    ['Database', data.database],
    ['MQTT', data.mqtt],
    ['Vision', data.vision],
    ['Camera', data.camera],
    ['Camera host', data.cameraStreamHost ?? t('Not reported')],
    ['Last frame', date(data.lastFrameAt)],
    ['Frame size', frameSize],
    ['Configured still', data.configuredStillWidth && data.configuredStillHeight ? `${data.configuredStillWidth}×${data.configuredStillHeight}` : t('Not reported')],
    ['Last still', data.lastStillWidth && data.lastStillHeight ? `${data.lastStillWidth}×${data.lastStillHeight}` : t('Not reported')],
    ['Last still error', data.lastStillError ?? t('Not reported')],
    ['Preview fallback', data.previewFallbackAllowed == null ? t('Not reported') : String(data.previewFallbackAllowed)],
    ['Sensor PID', data.sensorPid ?? t('Not reported')],
    ['Camera failures', data.cameraConsecutiveFailures == null ? t('Not reported') : number(data.cameraConsecutiveFailures)],
    ['Last inspection', date(data.lastInspectionAt)],
    ['Model', `${text(data.modelStatus)} ${data.modelName ?? ''} ${data.modelVersion ?? ''}`.trim()],
    ['Model configured', model ? String(model.configured) : t('Not reported')],
    ['Model loaded', model ? String(model.loaded) : t('Not reported')],
    ['Model backend', model?.backend ?? t('Not reported')],
    ['Model task', model?.task ?? t('Not reported')],
    ['Model artifact', model?.artifact ?? t('Not registered')],
    ['Last inference', model?.lastInferenceLatencyMs == null ? t('Not measured') : `${number(model.lastInferenceLatencyMs)} ms`],
    ['Model load error', model?.loadError ?? t('None')],
    ['Confidence threshold', model?.confidenceThreshold == null ? t('Not reported') : number(model.confidenceThreshold)],
    ['ESP32', data.esp32],
    ['SignalR hub', data.signalR],
    ['SignalR client', connection],
    ['Simulation enabled', String(data.simulation.enabled)],
    ['Simulation active', String(data.simulation.active)],
    ['Simulation latch', String(data.simulation.latched)],
    ['In-flight simulated products', number(data.simulation.inFlightSimulatedProducts)],
    ['Hardware actuation', String(data.hardwareActuationEnabled)],
    ['Environment', data.environment],
    ['Active recipe', data.activeRecipe ?? t('None')],
    ['Active recipe version', data.activeRecipeVersion == null ? t('Not reported') : number(data.activeRecipeVersion)],
    ['Time of flight', data.timeOfFlightSource],
    ['Camera stream', data.cameraStreamUrl ?? t('Not configured')],
  ] : []

  return (
    <div className="flex flex-col gap-4">
      <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
        <StatusPill label="API" value={text(data?.api ?? '…')} tone={healthTone(data?.api)} />
        <StatusPill label={t("Database")} value={text(data?.database ?? '…')} tone={healthTone(data?.database)} />
        <StatusPill label="MQTT" value={text(data?.mqtt ?? '…')} tone={healthTone(data?.mqtt)} />
        <StatusPill label={t("Vision")} value={text(data?.vision ?? '…')} tone={healthTone(data?.vision)} />
        <StatusPill label={t("Camera")} value={text(data?.camera ?? '…')} tone={healthTone(data?.camera)} />
        <StatusPill label={t("Model")} value={text(data?.modelStatus ?? '…')} tone={healthTone(data?.modelStatus)} />
        <StatusPill label={t("Backend")} value={model?.backend ?? t('Not reported')} tone={model?.loaded ? 'good' : 'warn'} />
        <StatusPill label="SignalR" value={text(connection)} tone={healthTone(connection)} />
      </div>
      <Panel title={t("Configuration")}>
        <dl className="grid grid-cols-1 gap-2 text-sm md:grid-cols-2">
          {rows.map(([label, value]) => (
            <div key={label} className="flex justify-between gap-4 border-b border-card-border py-1"><dt className="text-text-muted">{text(label)}</dt><dd className="min-w-0 break-words text-right">{['API', 'Database', 'MQTT', 'Vision', 'Camera', 'SignalR hub', 'SignalR client', 'Time of flight', 'Model configured', 'Model loaded', 'Simulation enabled', 'Simulation active', 'Simulation latch', 'Hardware actuation', 'Preview fallback'].includes(label) ? text(value) : value}</dd></div>
          ))}
        </dl>
        <p className="mt-3 text-xs text-text-muted">{t("Secrets, passwords, connection strings, and certificates are not part of this response. Vision is ONLINE only after a health response, OFFLINE when that request fails, and NOT_CONFIGURED when no health URL is set. Camera stays UNKNOWN until Vision is ONLINE. A model is loaded only when Vision reports that the artifact loaded. Latency is omitted until an inspection actually runs.")}</p>
        {report.error && <p className="mt-3 text-sm text-danger-light">{text(report.error)}</p>}
      </Panel>
      <Panel title={t('Recent events')}>
        <ul className="space-y-3 text-sm">{room.data?.recentEvents.map(event => <li key={`${event.createdAt}-${event.eventType}-${event.message}`}><p className="text-xs text-text-muted">{date(event.createdAt)} · {event.eventType}</p><p>{event.message}</p></li>)}</ul>
        {room.data?.recentEvents.length === 0 && <p className="text-sm text-text-muted">{t('Waiting for a system event.')}</p>}
      </Panel>
      <LiveLogFeed />
    </div>
  )
}
