import type { DisplayText } from '../../i18n/language'
import { useLanguage } from '../../i18n/LanguageContext'
import { useState } from 'react'
import { useDashboardState, useDashboardDispatch } from '../../context/DashboardContext'
import { api, getApiErrorMessage } from '../../api/client'

const alertLabels: Record<string, string> = {
  EMERGENCY_STOP: 'Emergency stop',
  HIGH_TEMPERATURE: 'High temperature',
  NO_BARCODE_DETECTED: 'Barcode unreadable',
}

export default function AlertsPanel() {
  const { t, text, time } = useLanguage()
  const { activeAlerts } = useDashboardState()
  const dispatch = useDashboardDispatch()
  const [resolvingId, setResolvingId] = useState<number | null>(null)
  const [error, setError] = useState<DisplayText | null>(null)

  const panelAlerts = activeAlerts.filter(a => a.alertType !== 'FIRE')

  const handleResolve = async (id: number) => {
    setResolvingId(id)
    setError(null)
    try {
      const alert = await api.resolveAlert(id)
      dispatch({ type: 'UPDATE_ALERT', payload: alert })
    } catch (resolveError) {
      setError(getApiErrorMessage(resolveError, 'The alarm could not be resolved.'))
    } finally {
      setResolvingId(null)
    }
  }

  return (
    <section className="surface-panel">
      <h2 className="mb-4 font-semibold">{t('Active alerts')}</h2>
          {error && <p role="alert" className="mb-3 text-xs text-danger-light">{text(error)}</p>}
          {panelAlerts.length === 0 ? (
            <p className="text-sm text-text-muted">{t("No active alerts")}</p>
          ) : (
            <div className="flex flex-col gap-3">
              {panelAlerts.map(alert => (
                <div key={alert.id} className="flex items-start justify-between gap-3 p-3 rounded-lg bg-warning/10 border border-warning/30">
                  <div className="flex flex-col gap-0.5">
                    <span className="text-xs font-medium text-warning-light">
                      {text(alertLabels[alert.alertType] ?? alert.alertType)}
                    </span>
                    <span className="text-xs text-text-muted">
                      {time(alert.triggeredAt)}
                    </span>
                  </div>
                  {(
                    <button
                      onClick={() => handleResolve(alert.id)}
                      disabled={resolvingId === alert.id}
                      className="px-2 py-1 text-xs rounded bg-warning/20 text-warning-light hover:bg-warning/30 disabled:opacity-50 transition-colors whitespace-nowrap"
                    >
                      {resolvingId === alert.id ? '...' : t('Resolve')}
                    </button>
                  )}
                </div>
              ))}
            </div>
          )}
    </section>
  )
}
