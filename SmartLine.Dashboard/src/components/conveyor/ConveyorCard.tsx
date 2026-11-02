import { useLanguage } from '../../i18n/LanguageContext'
import { FaPlay, FaPause, FaFan } from 'react-icons/fa'
import { useDashboardState } from '../../context/DashboardContext'
import SpeedSlider from './SpeedSlider'
import { useControlAvailability } from '../../context/PlantContext'
import EmergencyStop from './EmergencyStop'

export default function ConveyorCard() {
  const { number, t, time } = useLanguage()
  const { conveyor } = useDashboardState()
  const available = useControlAvailability()

  if (!conveyor) {
    return (
      <div className="bg-card rounded-xl border border-card-border p-5 animate-pulse">
        <div className="h-6 bg-card-border rounded w-1/2 mb-4" />
        <div className="h-16 bg-card-border rounded" />
      </div>
    )
  }

  return (
    <div className="bg-card rounded-xl border border-card-border p-5 flex flex-col gap-4">
      <div className="flex items-center justify-between">
        <h2 className="text-base font-semibold">{t("Conveyor status")}</h2>
        <div className="flex items-center gap-2">
          {conveyor.isRunning ? (
            <span className="flex items-center gap-1.5 text-sm text-success-light">
              <FaPlay className="text-xs" />{t("Running")}</span>
          ) : (
            <span className="flex items-center gap-1.5 text-sm text-danger-light">
              <FaPause className="text-xs" />{t("Stopped")}</span>
          )}
        </div>
      </div>

      <fieldset disabled={!available} className="min-w-0 space-y-4">
      <legend className="sr-only">{t("Conveyor controls")}</legend>
      <div className="flex items-center gap-6">
        <div className="text-center">
          <div className="text-4xl font-bold tabular-nums">{number(conveyor.speed)}</div>
          <div className="text-xs text-text-muted mt-1">{t("Speed")}</div>
        </div>

        <div className="flex-1">
          <SpeedSlider
            currentSpeed={conveyor.speed}
            isRunning={conveyor.isRunning}
            isEmergencyStopped={conveyor.isEmergencyStopped}
          />
        </div>
      </div>

      {(conveyor.isEmergencyStopped || conveyor.isFireDetected || conveyor.isFireGenerationFault) && (
        <div className="flex flex-wrap gap-2 text-xs">
          {conveyor.isEmergencyStopped && (
            <span className="rounded-full border border-danger/30 bg-danger/10 px-2.5 py-1 font-medium text-danger-light">{t("Emergency latch active")}</span>
          )}
          {conveyor.isFireDetected && (
            <span className="rounded-full border border-warning/30 bg-warning/10 px-2.5 py-1 font-medium text-warning-light">{t("Fire sensor active")}</span>
          )}
          {conveyor.isFireGenerationFault && (
            <span className="rounded-full border border-danger/30 bg-danger/10 px-2.5 py-1 font-medium text-danger-light">{t("Fire sensor must be reprovisioned")}</span>
          )}
        </div>
      )}

      <div className="flex items-center justify-between pt-2 border-t border-card-border">
        <div className="flex items-center gap-2 text-sm">
          <FaFan className={`${conveyor.fanActive ? 'text-primary-light animate-spin' : 'text-text-muted'}`} />
          <span className="text-text-muted">{t("Fan:")}{' '}{conveyor.fanActive ? t('On') : t('Off')}
          </span>
        </div>

        <EmergencyStop
          isRunning={conveyor.isRunning}
          isEmergencyStopped={conveyor.isEmergencyStopped}
          isFireDetected={conveyor.isFireDetected}
          isFireGenerationFault={conveyor.isFireGenerationFault}
        />
      </div>

      </fieldset>
      <div className="text-xs text-text-muted text-right">{t("Last updated:")}{' '}{time(conveyor.lastUpdated)}
      </div>
    </div>
  )
}
