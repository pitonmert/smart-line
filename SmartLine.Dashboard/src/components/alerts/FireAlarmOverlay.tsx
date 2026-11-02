import type { DisplayText } from '../../i18n/language'
import { useLanguage } from '../../i18n/LanguageContext'
import { useState } from 'react'
import { FaFire } from 'react-icons/fa'
import { useDashboardState, useDashboardDispatch } from '../../context/DashboardContext'
import { api, getApiErrorMessage } from '../../api/client'

export default function FireAlarmOverlay({ readOnly = false }: { readOnly?: boolean }) {
  const { t, text } = useLanguage()
  const { activeAlerts, conveyor } = useDashboardState()
  const dispatch = useDashboardDispatch()
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<DisplayText | null>(null)

  const fireAlerts = activeAlerts.filter(a => a.alertType === 'FIRE' && a.isActive)
  if (fireAlerts.length === 0) return null

  const handleResolve = async () => {
    setLoading(true)
    setError(null)
    try {
      for (const alert of fireAlerts) {
        const resolved = await api.resolveAlert(alert.id)
        dispatch({ type: 'UPDATE_ALERT', payload: resolved })
      }
    } catch (resolveError) {
      setError(getApiErrorMessage(resolveError, 'The fire alarm could not be resolved.'))
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="mb-4 flex items-center justify-between gap-4 p-4 rounded-xl bg-danger/15 border border-danger/40 critical-overlay">
      <div className="flex items-center gap-3">
        <FaFire className="text-danger-light text-xl flex-shrink-0 animate-pulse" />
        <div>
          <p className="text-sm font-bold text-danger-light">{t("FIRE ALARM")}</p>
          <p className="text-xs text-text-muted">
            {conveyor?.isFireDetected
              ? t('The fire sensor is active; the emergency latch cannot be reset.')
              : t('The sensor is clear. The alarm can be resolved.')}
          </p>
          {error && <p role="alert" className="mt-1 text-xs text-danger-light">{text(error)}</p>}
        </div>
      </div>
      {!readOnly && conveyor && !conveyor.isFireDetected && (
        <button
          onClick={handleResolve}
          disabled={loading}
          className="px-4 py-1.5 text-xs font-medium rounded-lg bg-danger hover:brightness-110 text-white disabled:opacity-50 transition-colors whitespace-nowrap"
        >
          {loading ? t('Resolving…') : t('Resolve alarm')}
        </button>
      )}
    </div>
  )
}
