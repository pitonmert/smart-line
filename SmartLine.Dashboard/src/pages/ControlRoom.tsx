import { useLanguage } from '../i18n/LanguageContext'
import { useClock } from '../hooks/useClock'
import { usePlant } from '../context/PlantContext'
import { useDashboardState } from '../context/DashboardContext'
import CameraStage from '../components/camera/CameraStage'
import InspectionDetails from '../components/inspection/InspectionDetails'
import ProductionCounters from '../components/inspection/ProductionCounters'
import { SimulationBanner } from '../components/inspection/ui'
import { decisionTone } from '../components/inspection/format'
import type { MessageKey } from '../i18n/catalog'
import type { AppPath } from '../navigation/routes'

export default function ControlRoom({ navigate }: { navigate: (path: AppPath) => void }) {
  const { t, text, date, number } = useLanguage()
  const { room, diagnostics, recipes } = usePlant()
  const { conveyor, activeAlerts, connection } = useDashboardState()
  const now = useClock()
  const inspection = room.data?.lastInspection
  const recipe = recipes.data?.find(item => item.isActive)
  const report = diagnostics.data
  const stale = !!room.error || !!diagnostics.error || (!!room.data && (connection !== 'connected' || [room.lastSuccessAt, diagnostics.lastSuccessAt].some(at => at !== null && now - at > 30_000)))
  const problems: MessageKey[] = []
  if (room.error || diagnostics.error) problems.push('API unavailable')
  if (connection !== 'connected') problems.push('Live updates unavailable')
  if (conveyor?.isEmergencyStopped) problems.push('Emergency latch active')
  if (conveyor?.isFireDetected) problems.push('Fire sensor active')
  if (conveyor?.isFireGenerationFault) problems.push('Fire sensor must be reprovisioned')
  if (report) {
    if (!['healthy', 'mock'].includes(report.api.toLowerCase())) problems.push('API unavailable')
    if (!['healthy', 'mock'].includes(report.database.toLowerCase())) problems.push('Database')
    if (!['healthy', 'online', 'mock'].includes(report.vision.toLowerCase())) problems.push('Vision unavailable')
    if (!['connected', 'mock'].includes(report.mqtt.toLowerCase())) problems.push('MQTT unavailable')
    if (!['online', 'mock'].includes(report.esp32.toLowerCase())) problems.push('Device unavailable')
    if (!['online', 'healthy', 'mock'].includes(report.camera.toLowerCase())) problems.push('Camera unavailable')
    if (!['loaded', 'mock'].includes(report.modelStatus.toLowerCase())) problems.push('Model unavailable')
  }
  const alerts = activeAlerts.filter(alert => alert.isActive)
  const tone = decisionTone(inspection?.decision)
  return <div className="production-page">
    <div className="page-heading"><div><p className="eyebrow">SmartLine</p><h1>{t('Production')}</h1></div>
      <span className="text-xs text-text-muted">{t('Inspection totals exclude simulation.')}</span></div>
    {room.data?.simulationActive && <SimulationBanner text={t('Simulation data is visible.')} />}
    {stale && <div role="status" className="connection-banner">{t('Connection lost · showing last known values')}
      <span>{t('Last successful update')}: {room.lastSuccessAt ? date(new Date(room.lastSuccessAt).toISOString()) : '—'}</span></div>}
    <ProductionCounters />
    <div className="monitor-grid">
      <section aria-label={t('Live preview')} className="min-w-0">
        <CameraStage
          sourceWidth={recipe?.definition.cameraProfile.sourceWidth || 240}
          sourceHeight={recipe?.definition.cameraProfile.sourceHeight || 176}
          roi={recipe?.definition.roi} />
      </section>
      <section className={`result-card result-${tone}`} aria-labelledby="last-evaluation-heading">
        <div className="section-heading"><h2 id="last-evaluation-heading">{t('Last evaluation')}</h2><span className="eyebrow">{t('Stored inspection')}</span></div>
        {inspection ? <>
          <div className="result-decision"><span aria-hidden="true">{tone === 'good' ? '✓' : tone === 'bad' ? '×' : '!'}</span>{text(inspection.decision)}</div>
          {inspection.simulation && <span className="text-sm font-semibold text-warning-light">{t('SIMULATION')}</span>}
          <div className="result-product">{inspection.productClass}</div>
          <dl className="result-summary">
            <div><dt>{t('Confidence')}</dt><dd>{number(inspection.confidence * 100, { maximumFractionDigits: 1 })}%</dd></div>
            <div><dt>{t('Destination')}</dt><dd>{inspection.destinationBin}</dd></div>
            <div><dt>{t('Processed')}</dt><dd>{date(inspection.processedAt)}</dd></div>
          </dl>
          <details className="inline-details"><summary>{t('Evaluation details')}</summary><InspectionDetails inspection={inspection} /></details>
        </> : <div className="empty-result"><span aria-hidden="true">—</span><h3>{room.data ? t('No evaluation yet') : t('Waiting for data')}</h3><p>{t('Results will appear here when the API records an inspection.')}</p></div>}
      </section>
    </div>
    <p className="text-xs text-text-muted">{t('Camera preview and the last evaluation may belong to different moments.')}</p>
    <div className="production-footer">
      <span>{t('Temperature')} <strong>{conveyor ? `${number(conveyor.temperature, { minimumFractionDigits: 1, maximumFractionDigits: 1 })}°C` : '—'}</strong></span>
      <span>{t('Humidity')} <strong>{conveyor ? `${number(conveyor.humidity)}%` : '—'}</strong></span>
      <span>{t('Fan:')} <strong>{conveyor ? t(conveyor.fanActive ? 'On' : 'Off') : '—'}</strong></span>
      <span>{t('Active alerts')} <strong>{number(alerts.length)}</strong></span>
    </div>
    {(problems.length > 0 || alerts.length > 0) && <section className="problem-strip" aria-label={t('System needs attention')}>
      <div className="section-heading"><h2>{t('System needs attention')}</h2><button type="button" className="text-link" onClick={() => navigate('/management/diagnostics')}>{t('View system details')} →</button></div>
      <ul>{[...new Set(problems)].map(problem => <li key={problem}>{t(problem)}</li>)}{alerts.map(alert => <li key={alert.id}>{text(alert.alertType)}</li>)}</ul>
    </section>}
  </div>
}
