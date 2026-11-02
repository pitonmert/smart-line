import { useApiAvailability, usePlant } from '../context/PlantContext'
import type { DisplayText } from '../i18n/language'
import type { Message } from '../i18n/language'
import { useLanguage } from '../i18n/LanguageContext'
import { useRef, useState, type FormEvent } from 'react'
import { api, getApiErrorMessage } from '../api/client'
import CameraStage from '../components/camera/CameraStage'
import InspectionDetails from '../components/inspection/InspectionDetails'
import { fieldClass } from '../components/inspection/format'
import { ErrorNote, Panel, SimulationBanner } from '../components/inspection/ui'

export default function InspectionPage() {
  const apiAvailable = useApiAvailability()
  const { room, recipes } = usePlant()
  const { message, t, text } = useLanguage()
  const requestId = useRef<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<DisplayText | null>(null)
  const [notice, setNotice] = useState<Message | null>(null)
  const inspection = room.data?.lastInspection
  const recipe = recipes.data?.find(item => item.isActive)
  const sourceWidth = inspection?.geometry?.sourceWidth || recipe?.definition.cameraProfile.sourceWidth || 240
  const sourceHeight = inspection?.geometry?.sourceHeight || recipe?.definition.cameraProfile.sourceHeight || 176

  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    const form = new FormData(event.currentTarget)
    if (busy || !apiAvailable) return
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
      setNotice({ key: '{code} recorded as an operator observation. {message}', params: { code: result.track.publicCode, message: result.actuation.message } })
      room.reload()
    } catch (reason) {
      setError(getApiErrorMessage(reason, 'Inspection was not recorded.'))
    } finally { setBusy(false) }
  }

  const inspectCamera = async () => {
    if (busy || !apiAvailable) return
    setBusy(true)
    setError(null)
    requestId.current ??= crypto.randomUUID()
    try {
      const result = await api.inspectVision(requestId.current)
      requestId.current = null
      setNotice({ key: '{code} recorded from Vision.', params: { code: result.track.publicCode } })
      room.reload()
    } catch (reason) { setError(getApiErrorMessage(reason, 'Vision inspection failed. Retry uses the same request.')) }
    finally { setBusy(false) }
  }

  return (
    <div className="flex flex-col gap-4">
      {inspection?.simulation && <SimulationBanner text={t("The last inspection was simulated.")} />}
      {(room.error || recipes.error) && <ErrorNote>{room.error || recipes.error}</ErrorNote>}
      {<button type="button" disabled={busy || !apiAvailable || !room.data || !!room.error} onClick={() => void inspectCamera()} className="rounded-lg bg-primary px-3 py-2 text-white disabled:opacity-50">{busy ? t('Inspecting…') : t('Inspect camera with Vision')}</button>}
      <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
        <CameraStage
          sourceWidth={sourceWidth}
          sourceHeight={sourceHeight}
          roi={recipe?.definition.roi}
          simulation={inspection?.simulation}
          title={inspection ? `${inspection.productClass} ${text(inspection.decision)}` : t('No inspection yet')}
        />
        <Panel title={t("Last inspection")}>
          {inspection ? (
            <div><p className="mb-3 text-lg">{inspection.productClass} · {text(inspection.decision)} · {inspection.destinationBin}</p><InspectionDetails inspection={inspection} /></div>
          ) : <p className="text-sm text-text-muted">{t("No inspection has been stored.")}</p>}
          <p className="mt-3 text-xs text-text-muted">{t("Depth is unavailable until VL53L1X integration. A missing model does not create a bounding box.")}</p>
        </Panel>
      </div>
      {(
        <Panel title={t("Manual inspection trigger")}>
          <p className="mb-3 text-xs text-text-muted">{t("This records an operator observation through the API. It does not claim a model produced the class or a box.")}</p>
          <form className="grid grid-cols-1 gap-3 md:grid-cols-3" onSubmit={submit}>
            <input name="productClass" required placeholder={t("Product class")} className={fieldClass} />
            <input name="colorClass" required placeholder={t("Color class")} className={fieldClass} />
            <input name="sizeClass" required placeholder={t("Size class")} className={fieldClass} />
            <input name="defects" placeholder={t("Defects, comma separated")} className={fieldClass} />
            <input name="confidence" required type="number" min="0" max="1" step="0.01" defaultValue="0.8" className={fieldClass} />
            <button type="submit" disabled={busy || !apiAvailable || !room.data || !!room.error} className="rounded-lg bg-primary px-3 py-2 text-sm font-medium text-white">{t("Record inspection")}</button>
          </form>
          {notice && <p className="mt-3 text-sm">{message(notice)}</p>}
          {error && <div className="mt-3"><ErrorNote>{error}</ErrorNote></div>}
        </Panel>
      )}
    </div>
  )
}
