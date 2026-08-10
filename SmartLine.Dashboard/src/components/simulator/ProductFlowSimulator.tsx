import { useState, useEffect, useRef } from 'react'
import { FaBox } from 'react-icons/fa'
import { api, getApiErrorMessage } from '../../api/client'
import { useDashboardState } from '../../context/DashboardContext'
import { generateBarcode, randomColor, VALID_COLORS } from './utils'

export default function ProductFlowSimulator() {
  const { conveyor } = useDashboardState()
  const isRunning = conveyor?.isRunning === true
  const [barcode, setBarcode] = useState('')
  const [color, setColor] = useState('RED')
  const [autoMode, setAutoMode] = useState(false)
  const [autoInterval, setAutoInterval] = useState(2)
  const [sentCount, setSentCount] = useState(0)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const intervalRef = useRef<number | null>(null)

  // Konveyör durduğunda auto mode'u kapat
  useEffect(() => {
    if (conveyor?.isRunning === false && autoMode) {
      setAutoMode(false)
    }
  }, [autoMode, conveyor?.isRunning])

  // Otomatik mod
  useEffect(() => {
    if (!autoMode) return
    const id = window.setInterval(async () => {
      try {
        await api.sendBarcodeData(generateBarcode(), randomColor())
        setSentCount(prev => prev + 1)
      } catch { /* auto modda hata yutulur */ }
    }, autoInterval * 1000)
    intervalRef.current = id
    return () => {
      clearInterval(id)
      intervalRef.current = null
    }
  }, [autoMode, autoInterval])

  const handleManualSend = async () => {
    const bc = barcode.trim() || generateBarcode()
    setLoading(true)
    setError(null)
    try {
      await api.sendBarcodeData(bc, color)
      setBarcode('')
    } catch (err) {
      setError(getApiErrorMessage(err, 'Gönderilemedi.'))
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="flex flex-col gap-3">
      <h3 className="flex items-center gap-2 text-sm font-semibold text-text">
        <FaBox className="text-primary-light" />
        Ürün Akışı
      </h3>

      {!isRunning && (
        <p className="text-xs text-warning-light">Konveyör çalışmıyor</p>
      )}

      <div className={!isRunning ? 'pointer-events-none opacity-40 select-none' : ''}>
      {/* Manuel mod */}
      <div className="flex flex-col gap-2">
        <label className="text-xs text-text-muted">Manuel Gönderim</label>
        <div className="flex gap-2">
          <input
            type="text"
            value={barcode}
            onChange={e => setBarcode(e.target.value)}
            placeholder="BC_X4Y9Z2K1"
            maxLength={50}
            className="flex-1 px-2 py-1.5 text-xs rounded bg-surface border border-card-border text-text placeholder:text-text-muted/50"
          />
          <button
            onClick={() => setBarcode(generateBarcode())}
            className="px-2 py-1.5 text-xs rounded bg-card-border/50 text-text-muted hover:text-text transition-colors"
            title="Rastgele barkod"
          >
            Rastgele
          </button>
        </div>
        <div className="flex gap-2">
          <select
            value={color}
            onChange={e => setColor(e.target.value)}
            className="flex-1 px-2 py-1.5 text-xs rounded bg-surface border border-card-border text-text"
          >
            {VALID_COLORS.map(c => (
              <option key={c} value={c}>{c}</option>
            ))}
          </select>
          <button
            onClick={handleManualSend}
            disabled={loading}
            className="px-4 py-1.5 text-xs font-medium rounded bg-primary hover:bg-primary-light text-white disabled:opacity-50 transition-colors"
          >
            {loading ? '...' : 'Ürün Geçir'}
          </button>
        </div>
      </div>

      {/* Otomatik mod */}
      <div className="flex flex-col gap-3 pt-2 border-t border-card-border">
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-2">
            <label className="text-xs text-text-muted">Otomatik Mod</label>
            <span className={`text-xs transition-opacity ${autoMode ? 'text-success-light opacity-100' : 'opacity-0'}`}>
              Aktif
            </span>
          </div>
          <button
            role="switch"
            aria-checked={autoMode}
            onClick={() => { setAutoMode(prev => !prev); if (!autoMode) setSentCount(0) }}
            className={`relative inline-flex w-10 h-5 rounded-full transition-colors ${
              autoMode ? 'bg-success' : 'bg-card-border'
            }`}
          >
            <span className={`absolute top-0.5 h-4 w-4 rounded-full bg-white shadow-sm transition-transform ${
              autoMode ? 'translate-x-[22px]' : 'translate-x-0.5'
            }`} />
          </button>
        </div>

        {autoMode && (
          <>
            <div className="flex items-center gap-2">
              <label className="text-xs text-text-muted whitespace-nowrap">Aralık:</label>
              <input
                type="range"
                min={1}
                max={5}
                value={autoInterval}
                onChange={e => setAutoInterval(Number(e.target.value))}
                className="flex-1 accent-primary-light"
              />
              <span className="text-xs text-text-muted w-6 text-right">{autoInterval}s</span>
            </div>
            <p className="text-xs text-text-muted">{sentCount} ürün gönderildi</p>
          </>
        )}
      </div>

      {error && <p className="text-xs text-danger-light">{error}</p>}
      </div>
    </div>
  )
}
