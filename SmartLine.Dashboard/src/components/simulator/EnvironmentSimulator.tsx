import { useState } from 'react'
import { FaThermometerHalf } from 'react-icons/fa'
import { api, getApiErrorMessage } from '../../api/client'

export default function EnvironmentSimulator() {
  const [temperature, setTemperature] = useState(25)
  const [humidity, setHumidity] = useState(50)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const send = async (temp: number, hum: number) => {
    setLoading(true)
    setError(null)
    try {
      await api.sendEnvironmentData(temp, hum)
    } catch (err) {
      setError(getApiErrorMessage(err, 'Gönderilemedi.'))
    } finally {
      setLoading(false)
    }
  }

  const handleSend = () => send(temperature, humidity)

  const handlePreset = (temp: number, hum?: number) => {
    const t = temp
    const h = hum ?? humidity
    setTemperature(t)
    if (hum !== undefined) setHumidity(h)
    send(t, h)
  }

  // Slider arka plan renk bantları: yeşil (<35) → sarı (35-40) → kırmızı (>40)
  // -40..80 aralığında 35°C = %62.5, 40°C = %66.7
  const tempGradient = 'linear-gradient(to right, #22c55e 0%, #22c55e 62.5%, #f59e0b 62.5%, #f59e0b 66.7%, #ef4444 66.7%, #ef4444 100%)'
  const humGradient = 'linear-gradient(to right, #22c55e 0%, #22c55e 70%, #f59e0b 70%, #f59e0b 100%)'

  return (
    <div className="flex flex-col gap-3">
      <h3 className="flex items-center gap-2 text-sm font-semibold text-text">
        <FaThermometerHalf className="text-warning-light" />
        Ortam Simülasyonu
      </h3>

      {/* Sıcaklık */}
      <div className="flex flex-col gap-1">
        <label className="text-xs text-text-muted">Sıcaklık</label>
        <div className="relative">
          <div className="absolute inset-x-0 top-1/2 -translate-y-1/2 h-1.5 rounded-full opacity-30 pointer-events-none" style={{ background: tempGradient }} />
          <input
            type="range"
            min={-40}
            max={80}
            value={temperature}
            onChange={e => setTemperature(Number(e.target.value))}
            className="relative w-full accent-primary-light z-10"
          />
        </div>
        <div className="flex items-center gap-2">
          <input
            type="number"
            min={-40}
            max={80}
            value={temperature}
            onChange={e => setTemperature(Math.min(80, Math.max(-40, Number(e.target.value))))}
            className="w-16 px-2 py-1 text-xs rounded bg-surface border border-card-border text-text"
          />
          <span className="text-xs text-text-muted">°C</span>
        </div>
      </div>

      {/* Nem */}
      <div className="flex flex-col gap-1">
        <label className="text-xs text-text-muted">Nem</label>
        <div className="relative">
          <div className="absolute inset-x-0 top-1/2 -translate-y-1/2 h-1.5 rounded-full opacity-30 pointer-events-none" style={{ background: humGradient }} />
          <input
            type="range"
            min={0}
            max={100}
            value={humidity}
            onChange={e => setHumidity(Number(e.target.value))}
            className="relative w-full accent-primary-light z-10"
          />
        </div>
        <div className="flex items-center gap-2">
          <input
            type="number"
            min={0}
            max={100}
            value={humidity}
            onChange={e => setHumidity(Math.min(100, Math.max(0, Number(e.target.value))))}
            className="w-16 px-2 py-1 text-xs rounded bg-surface border border-card-border text-text"
          />
          <span className="text-xs text-text-muted">%</span>
        </div>
      </div>

      {/* Gönder butonu */}
      <button
        onClick={handleSend}
        disabled={loading}
        className="w-full py-1.5 text-xs font-medium rounded bg-primary hover:bg-primary-light text-white disabled:opacity-50 transition-colors"
      >
        {loading ? 'Gönderiliyor...' : 'Gönder'}
      </button>

      {/* Preset butonları */}
      <div className="grid grid-cols-2 gap-2">
        <button onClick={() => handlePreset(25, 50)} disabled={loading} className="px-2 py-1.5 text-xs rounded bg-success/20 text-success-light hover:bg-success/30 disabled:opacity-50 transition-colors">
          Normal (25°C, %50)
        </button>
        <button onClick={() => handlePreset(38)} disabled={loading} className="px-2 py-1.5 text-xs rounded bg-warning/20 text-warning-light hover:bg-warning/30 disabled:opacity-50 transition-colors">
          Sıcak (38°C)
        </button>
        <button onClick={() => handlePreset(42)} disabled={loading} className="px-2 py-1.5 text-xs rounded bg-danger/20 text-danger-light hover:bg-danger/30 disabled:opacity-50 transition-colors">
          Alarm (42°C)
        </button>
        <button onClick={() => handlePreset(55)} disabled={loading} className="px-2 py-1.5 text-xs rounded bg-danger/20 text-danger-light hover:bg-danger/30 disabled:opacity-50 transition-colors">
          Aşırı (55°C)
        </button>
      </div>

      {error && <p className="text-xs text-danger-light">{error}</p>}
    </div>
  )
}
