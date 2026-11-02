import type { DisplayText } from '../../i18n/language'
import { useLanguage } from '../../i18n/LanguageContext'
import { useState } from 'react'
import { FaFire } from 'react-icons/fa'
import { api, getApiErrorMessage } from '../../api/client'

const FIRE_STATE_KEY = 'smartline.dev.fire-sensor-state'

interface FireSensorState {
  deviceEpoch: string
  fireGeneration: number
  isDetected: boolean
}

function createFireSensorState(): FireSensorState {
  return {
    deviceEpoch: crypto.randomUUID().replaceAll('-', ''),
    fireGeneration: 0,
    isDetected: false,
  }
}

function readFireSensorState(): FireSensorState {
  try {
    const value = localStorage.getItem(FIRE_STATE_KEY)
    if (value) {
      const parsed = JSON.parse(value) as Partial<FireSensorState>
      if (
        typeof parsed.deviceEpoch === 'string'
        && /^[a-f0-9]{32}$/i.test(parsed.deviceEpoch)
        && Number.isSafeInteger(parsed.fireGeneration)
        && (parsed.fireGeneration ?? -1) >= 0
        && typeof parsed.isDetected === 'boolean'
      ) {
        return parsed as FireSensorState
      }
    }
  } catch {
    // A fresh development identity is safer than reusing corrupt simulator state.
  }

  const initial = createFireSensorState()
  localStorage.setItem(FIRE_STATE_KEY, JSON.stringify(initial))
  return initial
}

function nextFireSensorState(isDetected: boolean): FireSensorState {
  const current = readFireSensorState()
  if (isDetected && !current.isDetected && current.fireGeneration >= 0xffffffff) {
    const rotated = { ...createFireSensorState(), fireGeneration: 1, isDetected: true }
    localStorage.setItem(FIRE_STATE_KEY, JSON.stringify(rotated))
    return rotated
  }

  const next = {
    ...current,
    fireGeneration:
      isDetected && !current.isDetected
        ? current.fireGeneration + 1
        : current.fireGeneration,
    isDetected,
  }
  localStorage.setItem(FIRE_STATE_KEY, JSON.stringify(next))
  return next
}

export default function FireAlarmSimulator() {
  const { t, text } = useLanguage()
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<DisplayText | null>(null)

  const sendFire = async (isDetected: boolean) => {
    setLoading(true)
    setError(null)
    try {
      const sensor = nextFireSensorState(isDetected)
      await api.sendFireAlert(isDetected, sensor.deviceEpoch, sensor.fireGeneration)
    } catch (err) {
      setError(getApiErrorMessage(err, 'Could not send.'))
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="flex flex-col gap-3">
      <h3 className="flex items-center gap-2 text-sm font-semibold text-text">
        <FaFire className="text-danger-light" />{t("Fire alarm")}</h3>

      <div className="grid grid-cols-2 gap-2">
        <button
          onClick={() => sendFire(true)}
          disabled={loading}
          className="w-full py-2 text-xs font-medium rounded bg-danger hover:brightness-110 text-white disabled:opacity-50 transition-colors"
        >
          {loading ? t('Sending…') : t('Trigger fire')}
        </button>
        <button
          onClick={() => sendFire(false)}
          disabled={loading}
          className="w-full py-2 text-xs font-medium rounded border border-success/30 bg-success/10 text-success-light hover:bg-success/20 disabled:opacity-50 transition-colors"
        >{t("Clear sensor")}</button>
      </div>

      {error && <p className="text-xs text-danger-light">{text(error)}</p>}
    </div>
  )
}
