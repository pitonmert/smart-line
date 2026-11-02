import { useLanguage } from '../../i18n/LanguageContext'
import { useDashboardState } from '../../context/DashboardContext'
import GaugeBase from './GaugeBase'

export default function EnvironmentPanel() {
  const { t } = useLanguage()
  const { conveyor } = useDashboardState()
  const temp = conveyor?.temperature
  const humidity = conveyor?.humidity

  return (
    <div className="bg-card rounded-xl border border-card-border p-5 flex flex-col">
      <h2 className="text-base font-semibold mb-4">{t("Environment")}</h2>
      {!conveyor ? <p className="text-sm text-text-muted">{t('Waiting for data')}</p> : <div className="flex items-center justify-around flex-1">
        <GaugeBase
          value={temp!}
          min={-40}
          max={80}
          unit="°C"
          label={t("Temperature")}
          thresholds={[
            { value: 35, color: '#f59e0b' },
            { value: 40, color: '#ef4444' },
          ]}
        />
        <div className="w-px self-stretch bg-card-border" />
        <GaugeBase
          value={humidity!}
          min={0}
          max={100}
          unit="%"
          label={t("Humidity")}
          defaultColor="#06b6d4"
          thresholds={[{ value: 70, color: '#f59e0b' }]}
        />
      </div>}
    </div>
  )
}
