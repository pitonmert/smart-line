import { usePlant } from '../context/PlantContext'
import { useLanguage } from '../i18n/LanguageContext'
import EnvironmentPanel from '../components/gauges/EnvironmentPanel'
import { Panel, StatusPill } from '../components/inspection/ui'
import { useDashboardState } from '../context/DashboardContext'

export default function SensorsPage() {
  const { diagnostics } = usePlant()
  const { number, t, text } = useLanguage()
  const { conveyor } = useDashboardState()
  return (
    <div className="flex flex-col gap-4">
      <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
        <StatusPill label={t("Temperature")} value={conveyor ? `${number(conveyor.temperature)} °C` : '…'} />
        <StatusPill label={t("Humidity")} value={conveyor ? `${number(conveyor.humidity)} %` : '…'} />
        <StatusPill label={t("Fan")} value={conveyor ? t(conveyor.fanActive ? 'On' : 'Off') : '—'} />
        <StatusPill label={t("Fire")} value={conveyor ? t(conveyor.isFireDetected ? 'Fire detected' : 'Clear') : '—'} tone={conveyor?.isFireDetected ? 'bad' : 'good'} />
      </div>
      <EnvironmentPanel />
      <Panel title={t("Time of flight")}>
        <p className="text-sm">{t("Source:")}{' '}{text(diagnostics.data?.timeOfFlightSource ?? 'NOT_INTEGRATED')}</p>
        <p className="mt-2 text-sm text-text-muted">{t("VL53L1X is not integrated. productPresent, distanceMm, triggerTimestamp, and sensorHealthy are not read from hardware. No distance is displayed as a measurement.")}</p>
      </Panel>
    </div>
  )
}
