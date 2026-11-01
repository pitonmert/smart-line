import { useState, useEffect, useRef } from 'react'
import { api, getApiErrorMessage } from '../../api/client'
import { useDashboardDispatch } from '../../context/DashboardContext'

interface Props {
  currentSpeed: number
  isRunning: boolean
  isEmergencyStopped: boolean
}

export default function SpeedSlider({ currentSpeed, isRunning, isEmergencyStopped }: Props) {
  const [localSpeed, setLocalSpeed] = useState(currentSpeed)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const isDraggingRef = useRef(false)
  const isDirty = localSpeed !== currentSpeed
  const dispatch = useDashboardDispatch()

  useEffect(() => {
    if (!isDraggingRef.current) setLocalSpeed(currentSpeed)
  }, [currentSpeed])

  const handleApply = async () => {
    setLoading(true)
    setError(null)
    try {
      const status = await api.updateSpeed(localSpeed)
      dispatch({ type: 'UPDATE_CONVEYOR', payload: status })
    } catch (err) {
      setError(getApiErrorMessage(err, 'Hız güncellenemedi.'))
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="flex flex-col gap-2">
      <input
        type="range"
        min={0}
        max={100}
        value={localSpeed}
        onChange={e => { setLocalSpeed(Number(e.target.value)); setError(null) }}
        onPointerDown={() => { isDraggingRef.current = true }}
        onPointerUp={() => { isDraggingRef.current = false }}
        disabled={isRunning || isEmergencyStopped || loading}
        className="w-full accent-primary-light disabled:opacity-40"
      />
      <div className="flex items-center justify-between">
        <span className="text-xs text-text-muted">{localSpeed}%</span>
        {isDirty && (
          <button
            onClick={handleApply}
            disabled={loading || isRunning || isEmergencyStopped}
            className="text-xs px-3 py-1 rounded bg-primary hover:bg-primary-light text-white disabled:opacity-50 transition-colors"
          >
            {loading ? 'Gönderiliyor...' : 'Uygula'}
          </button>
        )}
      </div>
      {error && <p className="text-xs text-danger-light">{error}</p>}
    </div>
  )
}
