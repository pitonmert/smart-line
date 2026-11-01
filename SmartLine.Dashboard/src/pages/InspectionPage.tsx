import { useRef, useState, type FormEvent } from 'react'
import { api, getApiErrorMessage } from '../api/client'
import CameraStage from '../components/camera/CameraStage'
import { decisionTone, fieldClass, formatTime } from '../components/inspection/format'
import { ErrorNote, Panel, SimulationBanner } from '../components/inspection/ui'
import { usePolling } from '../hooks/usePolling'

export default function InspectionPage() {
  const room = usePolling(() => api.getControlRoom(), 4000)
  const recipes = usePolling(() => api.getRecipes(), 15000)
  const requestId = useRef<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const inspection = room.data?.lastInspection
  const recipe = recipes.data?.find(item => item.isActive)
  const sourceWidth = inspection?.geometry?.sourceWidth || recipe?.definition.cameraProfile.sourceWidth || 240
  const sourceHeight = inspection?.geometry?.sourceHeight || recipe?.definition.cameraProfile.sourceHeight || 176

  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    const form = new FormData(event.currentTarget)
    if (busy) return
    setBusy(true)
    setError(null)
    try {
      const result = await api.submitInspection({
        productClass: String(form.get('productClass') || ''),
        colorClass: String(form.get('colorClass') || ''),
        sizeClass: String(form.get('sizeClass') || ''),
        defectClasses: String(form.get('defects') || '').split(',').map(item => item.trim()).filter(Boolean),
        confidence: Number(form.get('confidence')),
        triggerSource: 'api',
        simulation: false,
        sourceWidth,
        sourceHeight,
        boundingBoxes: [],
      })
      setMessage(`${result.track.publicCode} recorded as an operator observation. ${result.actuation.message}`)
      room.reload()
    } catch (reason) {
      setError(getApiErrorMessage(reason, 'Inspection was not recorded.'))
    } finally { setBusy(false) }
  }

  const inspectCamera = async () => {
    if (busy) return
    setBusy(true)
    setError(null)
    requestId.current ??= crypto.randomUUID()
    try {
      const result = await api.inspectVision(requestId.current)
      requestId.current = null
      setMessage(`${result.track.publicCode} recorded from Vision.`)
      room.reload()
    } catch (reason) { setError(getApiErrorMessage(reason, 'Vision inspection failed. Retry uses the same request.')) }
    finally { setBusy(false) }
  }

  return (
    <div className="flex flex-col gap-4">
      {inspection?.simulation && <SimulationBanner text="The last inspection was simulated." />}
      {(room.error || recipes.error) && <ErrorNote>{room.error || recipes.error}</ErrorNote>}
      {<button type="button" disabled={busy || !!room.error} onClick={() => void inspectCamera()} className="rounded-lg bg-primary px-3 py-2 text-white disabled:opacity-50">{busy ? 'Inspecting…' : 'Inspect camera with Vision'}</button>}
      <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
        <CameraStage
          streamUrl={import.meta.env.VITE_CAMERA_STREAM_URL || recipe?.definition.cameraProfile.streamUrl || 'http://192.168.1.50:81/stream'}
          sourceWidth={sourceWidth}
          sourceHeight={sourceHeight}
          roi={recipe?.definition.roi}
          simulation={inspection?.simulation}
          items={(inspection?.boundingBoxes ?? []).map(box => ({
            ...box,
            tone: inspection?.decision === 'REJECT' ? 'reject' : inspection?.decision === 'MANUAL_REVIEW' ? 'review' : 'pass',
            label: box.label || inspection?.productClass || 'object',
          }))}
          title={inspection ? `${inspection.productClass} ${inspection.decision}` : 'No inspection yet'}
        />
        <Panel title="Last inspection">
          {inspection ? (
            <dl className="grid grid-cols-2 gap-2 text-sm">
              <div>Class</div><div>{inspection.productClass}</div>
              <div>Color</div><div>{inspection.colorClass}</div>
              <div>Size</div><div>{inspection.sizeClass}</div>
              <div>Defects</div><div>{inspection.defectClasses.join(', ') || 'None'}</div>
              <div>Quality</div><div>{inspection.qualityStatus}</div>
              <div>Confidence</div><div>{inspection.confidence}</div>
              <div>Decision</div><div className={decisionTone(inspection.decision) === 'bad' ? 'text-danger-light' : ''}>{inspection.decision}</div>
              <div>Destination</div><div>{inspection.destinationBin}</div>
              <div>Model</div><div>{inspection.modelName ?? 'none'} {inspection.modelVersion ?? ''}</div>
              <div>Latency</div><div>{inspection.processingLatencyMs ?? '—'} ms</div>
              <div>Visual size</div><div>{inspection.visualWidth ?? '—'} × {inspection.visualHeight ?? '—'} px</div>
              <div>Physical estimate</div><div>{inspection.estimatedPhysicalDimensions?.widthMm ?? '—'} × {inspection.estimatedPhysicalDimensions?.heightMm ?? '—'} mm visual</div>
              <div>Processed</div><div>{formatTime(inspection.processedAt)}</div>
            </dl>
          ) : <p className="text-sm text-text-muted">No inspection has been stored.</p>}
          <p className="mt-3 text-xs text-text-muted">Depth is unavailable until VL53L1X integration. A missing model does not create a bounding box.</p>
        </Panel>
      </div>
      {(
        <Panel title="Manual inspection trigger">
          <p className="mb-3 text-xs text-text-muted">This records an operator observation through the API. It does not claim a model produced the class or a box.</p>
          <form className="grid grid-cols-1 gap-3 md:grid-cols-3" onSubmit={submit}>
            <input name="productClass" required placeholder="Product class" className={fieldClass} />
            <input name="colorClass" required placeholder="Color class" className={fieldClass} />
            <input name="sizeClass" required placeholder="Size class" className={fieldClass} />
            <input name="defects" placeholder="Defects, comma separated" className={fieldClass} />
            <input name="confidence" required type="number" min="0" max="1" step="0.01" defaultValue="0.8" className={fieldClass} />
            <button type="submit" disabled={busy || !!room.error} className="rounded-lg bg-primary px-3 py-2 text-sm font-medium text-white">Record inspection</button>
          </form>
          {message && <p className="mt-3 text-sm">{message}</p>}
          {error && <div className="mt-3"><ErrorNote>{error}</ErrorNote></div>}
        </Panel>
      )}
    </div>
  )
}
