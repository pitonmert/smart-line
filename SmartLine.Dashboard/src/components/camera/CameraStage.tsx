import { useLanguage } from '../../i18n/LanguageContext'
import { api } from '../../api/client'
import { useEffect, useRef, useState } from 'react'
import { mapSourceBox, normalizedRoiToSource, type SourceBox } from '../../lib/overlay'

export interface OverlayItem extends SourceBox {
  label: string
  tone: 'pass' | 'reject' | 'review' | 'roi'
}

interface Props {
  sourceWidth: number
  sourceHeight: number
  roi?: { x: number; y: number; width: number; height: number } | null
  title?: string
  simulation?: boolean
}

const stroke = { pass: '#4ade80', reject: '#f87171', review: '#fbbf24', roi: '#60a5fa' }

// The preview is independent of stored inspections; only the configured ROI is drawn.
export default function CameraStage({ sourceWidth, sourceHeight, roi, title, simulation }: Props) {
  const { t, text } = useLanguage()
  const frameRef = useRef<HTMLDivElement>(null)
  const canvasRef = useRef<HTMLCanvasElement>(null)
  const [size, setSize] = useState({ width: 0, height: 0 })
  const [preview, setPreview] = useState<string>()
  const [camera, setCamera] = useState<'checking' | 'online' | 'offline'>('checking')

  useEffect(() => {
    let active = true
    let current: string | undefined
    let timer: number
    const refresh = async () => {
      try {
        if (!document.hidden) {
          const blob = await api.readCameraPreview()
          if (!active) return
          const next = URL.createObjectURL(blob)
          if (current) URL.revokeObjectURL(current)
          current = next
          setPreview(next)
          setCamera('online')
        }
      } catch { if (active) setCamera('offline') }
      finally { if (active) timer = window.setTimeout(refresh, 1000) }
    }
    void refresh()
    return () => { active = false; window.clearTimeout(timer); if (current) URL.revokeObjectURL(current) }
  }, [])

  useEffect(() => {
    const element = frameRef.current
    if (!element) return
    const observer = new ResizeObserver(() => setSize({ width: element.clientWidth, height: element.clientHeight }))
    observer.observe(element)
    setSize({ width: element.clientWidth, height: element.clientHeight })
    return () => observer.disconnect()
  }, [])

  useEffect(() => {
    const canvas = canvasRef.current
    if (!canvas) return
    const ratio = window.devicePixelRatio || 1
    canvas.width = Math.max(1, Math.floor(size.width * ratio))
    canvas.height = Math.max(1, Math.floor(size.height * ratio))
    const context = canvas.getContext('2d')
    if (!context) return
    context.setTransform(ratio, 0, 0, ratio, 0, 0)
    context.clearRect(0, 0, size.width, size.height)
    // Never draw stored inspection boxes over a newer preview frame.
    const drawable: OverlayItem[] = []
    if (roi) drawable.push({ ...normalizedRoiToSource(roi, sourceWidth, sourceHeight), label: 'ROI', tone: 'roi' })
    for (const item of drawable) {
      const mapped = mapSourceBox(item, sourceWidth, sourceHeight, size.width, size.height)
      if (!mapped.visible) continue
      context.strokeStyle = stroke[item.tone]
      context.lineWidth = item.tone === 'roi' ? 1 : 2
      context.setLineDash(item.tone === 'roi' ? [6, 4] : [])
      context.strokeRect(mapped.x, mapped.y, mapped.width, mapped.height)
      context.setLineDash([])
      context.font = '12px sans-serif'
      context.fillStyle = stroke[item.tone]
      context.fillText(item.label, mapped.x + 4, Math.max(14, mapped.y - 4))
    }
  }, [roi, size, sourceHeight, sourceWidth])

  const fullscreen = () => {
    const element = frameRef.current
    if (!element) return
    if (document.fullscreenElement) void document.exitFullscreen()
    else void element.requestFullscreen()
  }

  return (
    <div className="flex h-full min-h-[320px] flex-col overflow-hidden rounded-xl border border-card-border bg-black">
      <div className="flex items-center justify-between gap-3 px-3 py-2 text-xs text-slate-200">
        <div className="flex items-center gap-2">
          <span className={`h-2 w-2 rounded-full ${camera === 'online' ? 'bg-success-light' : camera === 'offline' ? 'bg-danger-light' : 'bg-warning-light'}`} />
          <span>{t("Camera")}{' '}{text(camera)}</span>
          <span className="text-slate-400">{sourceWidth}×{sourceHeight}{' '}{t("source")}</span>
          {simulation && <span className="rounded bg-warning/20 px-1.5 py-0.5 font-semibold text-warning-light">{t("SIMULATION")}</span>}
        </div>
        <button type="button" onClick={fullscreen} className="rounded border border-white/20 px-2 py-1 hover:bg-white/10">{t("Full screen")}</button>
      </div>
      <div ref={frameRef} className="relative min-h-0 flex-1">
        {!preview && <p className="absolute inset-0 flex items-center justify-center text-sm text-slate-300">{t(camera === 'offline' ? 'No image available' : 'Waiting for data')}</p>}
        {camera === 'offline' && preview && <div role="status" className="absolute inset-x-0 top-10 z-10 bg-black/80 px-3 py-2 text-center text-sm text-amber-200">{t('Camera disconnected · last frame')}</div>}
        {preview && <img
          src={preview}
          alt={t("Live conveyor camera")}
          className="h-full w-full object-contain"
          onLoad={() => setCamera('online')}
          onError={() => setCamera('offline')}
        />}
        <canvas ref={canvasRef} className="pointer-events-none absolute inset-0" />
        <div className="pointer-events-none absolute top-2 left-3 rounded bg-black/70 px-2 text-xs text-white">{t("Live preview · result boxes are not overlaid")}</div>
        {title && <div className="pointer-events-none absolute bottom-3 left-3 rounded bg-black/70 px-2 py-1 text-xs text-white">{title}</div>}
      </div>
    </div>
  )
}
