import { useState } from 'react'
import { FaTimes } from 'react-icons/fa'
import { useDashboardState, useDashboardDispatch } from '../../context/DashboardContext'
import { api, getApiErrorMessage } from '../../api/client'
import { useAuth } from '../../auth/AuthContext'

const alertLabels: Record<string, string> = {
  EMERGENCY_STOP: 'Acil Durdurma',
  HIGH_TEMPERATURE: 'Yüksek Sıcaklık',
  NO_BARCODE_DETECTED: 'Barkod Okunamadı',
}

export default function AlertsPanel({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { activeAlerts } = useDashboardState()
  const dispatch = useDashboardDispatch()
  const [resolvingId, setResolvingId] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)
  const { hasMinimumRole } = useAuth()
  const canResolve = hasMinimumRole('Operator')

  const panelAlerts = activeAlerts.filter(a => a.alertType !== 'FIRE')

  const handleResolve = async (id: number) => {
    setResolvingId(id)
    setError(null)
    try {
      const alert = await api.resolveAlert(id)
      dispatch({ type: 'UPDATE_ALERT', payload: alert })
    } catch (resolveError) {
      setError(getApiErrorMessage(resolveError, 'Alarm çözülemedi.'))
    } finally {
      setResolvingId(null)
    }
  }

  return (
    <div
      className={`flex-shrink-0 flex flex-col bg-card border-l border-card-border transition-[width] duration-300 ease-in-out overflow-hidden ${
        open ? 'w-80' : 'w-0'
      }`}
    >
      <div className="w-80 h-full flex flex-col">
        <div className="flex items-center justify-between px-4 py-3 border-b border-card-border">
          <h2 className="text-sm font-semibold text-text">Alarmlar</h2>
          <button
            onClick={onClose}
            className="p-1 rounded text-text-muted hover:text-text hover:bg-card-border/30 transition-colors"
          >
            <FaTimes />
          </button>
        </div>

        <div className="flex-1 overflow-y-auto p-4">
          {error && <p role="alert" className="mb-3 text-xs text-danger-light">{error}</p>}
          {panelAlerts.length === 0 ? (
            <p className="text-sm text-text-muted">Aktif alarm yok</p>
          ) : (
            <div className="flex flex-col gap-3">
              {panelAlerts.map(alert => (
                <div key={alert.id} className="flex items-start justify-between gap-3 p-3 rounded-lg bg-warning/10 border border-warning/30">
                  <div className="flex flex-col gap-0.5">
                    <span className="text-xs font-medium text-warning-light">
                      {alertLabels[alert.alertType] ?? alert.alertType}
                    </span>
                    <span className="text-xs text-text-muted">
                      {new Date(alert.triggeredAt).toLocaleTimeString('tr-TR')}
                    </span>
                  </div>
                  {canResolve && (
                    <button
                      onClick={() => handleResolve(alert.id)}
                      disabled={resolvingId === alert.id}
                      className="px-2 py-1 text-xs rounded bg-warning/20 text-warning-light hover:bg-warning/30 disabled:opacity-50 transition-colors whitespace-nowrap"
                    >
                      {resolvingId === alert.id ? '...' : 'Çöz'}
                    </button>
                  )}
                </div>
              ))}
            </div>
          )}
        </div>
      </div>
    </div>
  )
}
