import type { DisplayText } from '../../i18n/language'
import { useLanguage } from '../../i18n/LanguageContext'
import { useState } from 'react'
import { FaExclamationTriangle, FaPause, FaPlay, FaUndo } from 'react-icons/fa'
import { api, getApiErrorMessage } from '../../api/client'
import { useDashboardDispatch } from '../../context/DashboardContext'

interface Props {
  isRunning: boolean
  isEmergencyStopped: boolean
  isFireDetected: boolean
  isFireGenerationFault: boolean
}

type Action = 'start' | 'stop' | 'emergency-stop' | 'emergency-reset'

export default function EmergencyStop({ isRunning, isEmergencyStopped, isFireDetected, isFireGenerationFault }: Props) {
  const { t, text } = useLanguage()
  const [loading, setLoading] = useState<Action | null>(null)
  const [error, setError] = useState<DisplayText | null>(null)
  const dispatch = useDashboardDispatch()

  const run = async (action: Action) => {
    if (action === 'emergency-stop' && !confirm(t('Enable the emergency-stop latch?'))) return

    setLoading(action)
    setError(null)
    try {
      const status = await {
        start: api.startConveyor,
        stop: api.stopConveyor,
        'emergency-stop': api.emergencyStopConveyor,
        'emergency-reset': api.resetEmergencyStop,
      }[action]()
      dispatch({ type: 'UPDATE_CONVEYOR', payload: status })
    } catch (actionError) {
      setError(getApiErrorMessage(actionError, 'The conveyor command could not be applied.'))
    } finally {
      setLoading(null)
    }
  }


  return (
    <div className="flex max-w-[15rem] flex-col items-end gap-2">
      <div className="flex flex-wrap justify-end gap-2">
        {isRunning ? (
          <button
            onClick={() => run('stop')}
            disabled={loading !== null}
            className="flex items-center gap-1.5 rounded bg-warning px-3 py-1.5 text-xs text-white transition-colors hover:brightness-110 disabled:opacity-50"
          >
            <FaPause className="text-[10px]" />
            {loading === 'stop' ? t('Stopping…') : t('Normal stop')}
          </button>
        ) : (
          <button
            onClick={() => run('start')}
            disabled={loading !== null || isEmergencyStopped || isFireDetected || isFireGenerationFault}
            className="flex items-center gap-1.5 rounded bg-success px-3 py-1.5 text-xs text-white transition-colors hover:bg-success-light disabled:opacity-50"
          >
            <FaPlay className="text-[10px]" />
            {loading === 'start' ? t('Starting…') : t('Start')}
          </button>
        )}

        <button
          onClick={() => run('emergency-stop')}
          disabled={loading !== null || isEmergencyStopped}
          className="flex items-center gap-1.5 rounded bg-danger px-3 py-1.5 text-xs font-semibold text-white transition-colors hover:brightness-110 disabled:opacity-50"
        >
          <FaExclamationTriangle className="text-[10px]" />
          {loading === 'emergency-stop' ? t('Stopping…') : t('Emergency stop')}
        </button>

        {isEmergencyStopped && (
          <button
            onClick={() => run('emergency-reset')}
            disabled={loading !== null || isFireDetected || isFireGenerationFault}
            title={
              isFireGenerationFault
                ? t('Reprovision the fire sensor before resetting the latch.')
                : isFireDetected
                  ? t('Clear the fire sensor before resetting the latch.')
                  : undefined
            }
            className="flex items-center gap-1.5 rounded border border-primary/30 bg-primary/10 px-3 py-1.5 text-xs text-primary-light transition-colors hover:bg-primary/20 disabled:opacity-50"
          >
            <FaUndo className="text-[10px]" />
            {loading === 'emergency-reset' ? t('Resetting…') : t('Reset latch')}
          </button>
        )}
      </div>
      {error && <p role="alert" className="text-right text-xs text-danger-light">{text(error)}</p>}
    </div>
  )
}
