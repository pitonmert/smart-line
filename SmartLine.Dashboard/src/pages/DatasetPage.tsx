import { useApiAvailability, usePlant } from '../context/PlantContext'
import type { DisplayText } from '../i18n/language'
import type { Message } from '../i18n/language'
import { useLanguage } from '../i18n/LanguageContext'
import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react'
import { api, getApiErrorMessage } from '../api/client'
import CameraStage from '../components/camera/CameraStage'
import { fieldClass, healthTone } from '../components/inspection/format'
import { ErrorNote, Panel, SimulationBanner, StatusPill } from '../components/inspection/ui'
import { usePolling } from '../hooks/usePolling'
import type { DatasetSample, DatasetSession, RecipeDefinition } from '../types/inspection'

const LIVE_PREVIEW = { width: 240, height: 176 }

interface Labels {
  productClass: string
  qualityLabel: string
  colorLabel: string
  sizeLabel: string
  defectLabels: string[]
  notes: string
}

const emptyLabels: Labels = {
  productClass: '',
  qualityLabel: 'GOOD',
  colorLabel: '',
  sizeLabel: '',
  defectLabels: [],
  notes: '',
}

function choose(options: { code: string }[], current: string) {
  if (options.length === 0) return current
  if (options.some(item => item.code === current)) return current
  return options[0]?.code ?? current
}

function labelsFromSession(session: DatasetSession): Labels {
  return {
    productClass: session.productClass,
    qualityLabel: session.qualityLabel || 'GOOD',
    colorLabel: session.colorLabel,
    sizeLabel: session.sizeLabel,
    defectLabels: session.defectLabels,
    notes: session.notes ?? '',
  }
}

export default function DatasetPage() {
  const apiAvailable = useApiAvailability()
  const { diagnostics, recipes } = usePlant()
  const { date, message, t, text } = useLanguage()
  const sessions = usePolling(() => api.getDatasetSessions(), 4000)
  const reloadSessions = sessions.reload
  const [error, setError] = useState<DisplayText | null>(null)
  const [notice, setNotice] = useState<Message | null>(null)
  const [labels, setLabels] = useState<Labels>(emptyLabels)
  const [recipeId, setRecipeId] = useState('')
  const [captureMode, setCaptureMode] = useState<'manual' | 'interval'>('manual')
  const [intervalMs, setIntervalMs] = useState(1000)
  const [simulation, setSimulation] = useState(false)
  const [busy, setBusy] = useState(false)
  const [intervalRunning, setIntervalRunning] = useState(false)
  const [localSession, setLocalSession] = useState<{ data: DatasetSession; baseline: DatasetSession[] | null } | null>(null)
  const labelsRef = useRef(labels)
  const sessionIdRef = useRef<string | null>(null)
  const inFlight = useRef(false)
  const intervalOn = useRef(false)
  const syncedSession = useRef<string | null>(null)

  const polledActive = sessions.data?.find(item => item.status === 'ACTIVE') ?? null
  // Yeni sunucu yanıtı yerel iyimser görüntünün yerini alır.
  const active = localSession && localSession.baseline === sessions.data ? localSession.data : polledActive
  const recipe = recipes.data?.find(item => item.id === (active?.recipeId ?? recipeId)) ?? null
  const sourceWidth = recipe?.definition.cameraProfile.sourceWidth || LIVE_PREVIEW.width
  const sourceHeight = recipe?.definition.cameraProfile.sourceHeight || LIVE_PREVIEW.height

  useEffect(() => { labelsRef.current = labels }, [labels])
  useEffect(() => { sessionIdRef.current = active?.sessionId ?? null }, [active?.sessionId])
  useEffect(() => { intervalOn.current = intervalRunning }, [intervalRunning])

  useEffect(() => {
    if (recipeId || !recipes.data?.length) return
    const selected = recipes.data.find(item => item.isActive) ?? recipes.data[0]
    if (selected) setRecipeId(selected.id)
  }, [recipeId, recipes.data])

  useEffect(() => {
    if (!active) {
      syncedSession.current = null
      return
    }
    if (syncedSession.current === active.sessionId) return
    syncedSession.current = active.sessionId
    setLabels(labelsFromSession(active))
  }, [active])

  useEffect(() => {
    if (!recipe || active) return
    setLabels(current => {
      const next = {
        ...current,
        productClass: choose(recipe.definition.productClasses, current.productClass),
        colorLabel: choose(recipe.definition.allowedColors, current.colorLabel),
        sizeLabel: choose(recipe.definition.sizeClasses, current.sizeLabel),
        qualityLabel: current.qualityLabel === 'DEFECTIVE' ? 'DEFECTIVE' : 'GOOD',
        defectLabels: current.defectLabels.filter(code => recipe.definition.defectClasses.some(item => item.code === code)),
      }
      const unchanged = next.productClass === current.productClass
        && next.colorLabel === current.colorLabel
        && next.sizeLabel === current.sizeLabel
        && next.qualityLabel === current.qualityLabel
        && next.defectLabels.join('|') === current.defectLabels.join('|')
      return unchanged ? current : next
    })
  }, [active, recipe])

  const captureFromCamera = useCallback(async (override?: Labels) => {
    const sessionId = sessionIdRef.current
    if (!apiAvailable || !sessionId || inFlight.current) return null
    const current = override ?? labelsRef.current
    inFlight.current = true
    setBusy(true)
    setError(null)
    try {
      const result = await api.captureDatasetFromCamera(sessionId, {
        productClass: current.productClass,
        qualityLabel: current.qualityLabel,
        colorLabel: current.colorLabel,
        sizeLabel: current.sizeLabel,
        defectLabels: current.defectLabels,
        notes: current.notes,
      })
      setLocalSession({ data: result.session, baseline: sessions.data })
      const width = result.sample.sourceWidth
      const height = result.sample.sourceHeight
      const size = width && height ? `${width}×${height}` : 'unknown size'
      const source = result.sample.captureSource ?? 'unknown source'
      const fallback = result.sample.previewFallback ? 'Preview fallback was used.' : ''
      setNotice({ key: 'Stored {size} from {source}. {count} capture(s). {fallback} Bounding boxes were not invented.', params: { size, source, count: result.session.imageCount, fallback } })
      reloadSessions()
      return result
    } catch (reason) {
      intervalOn.current = false
      setIntervalRunning(false)
      setError(getApiErrorMessage(reason, 'Camera frame was not stored.'))
      return null
    } finally {
      inFlight.current = false
      setBusy(false)
    }
  }, [apiAvailable, reloadSessions, sessions.data])

  useEffect(() => {
    if (!apiAvailable || sessions.error || diagnostics.error || document.hidden || !active) {
      setIntervalRunning(false)
      intervalOn.current = false
      return
    }
    if (!intervalRunning || active?.captureMode !== 'interval' || !active.intervalMs) return
    const ms = active.intervalMs
    const id = window.setInterval(() => {
      if (!intervalOn.current) return
      void captureFromCamera()
    }, ms)
    return () => window.clearInterval(id)
  }, [active?.captureMode, active?.intervalMs, active?.sessionId, captureFromCamera, intervalRunning, sessions.error, diagnostics.error, active, apiAvailable])

  useEffect(() => {
    const pause = () => { intervalOn.current = false; setIntervalRunning(false) }
    const visibility = () => { if (document.hidden) pause() }
    document.addEventListener('visibilitychange', visibility)
    window.addEventListener('offline', pause)
    return () => { document.removeEventListener('visibilitychange', visibility); window.removeEventListener('offline', pause); intervalOn.current = false }
  }, [])

  const start = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!apiAvailable) return
    setError(null)
    try {
      const session = await api.startDatasetSession({
        recipeId: recipeId || null,
        productClass: labels.productClass,
        qualityLabel: labels.qualityLabel,
        colorLabel: labels.colorLabel,
        sizeLabel: labels.sizeLabel,
        defectLabels: labels.defectLabels,
        notes: labels.notes,
        captureMode,
        intervalMs: captureMode === 'interval' ? intervalMs : null,
        simulation,
      })
      setLocalSession({ data: session, baseline: sessions.data })
      setNotice({ key: 'Session started. {status}', params: { status: session.labelStatus } })
      sessions.reload()
    } catch (reason) {
      setError(getApiErrorMessage(reason, 'Dataset session did not start.'))
    }
  }

  const stop = async () => {
    if (!active) return
    intervalOn.current = false
    setIntervalRunning(false)
    setError(null)
    try {
      await api.stopDatasetSession(active.sessionId)
      setLocalSession(null)
      setNotice({ key: 'Dataset session stopped.' })
      sessions.reload()
    } catch (reason) {
      setError(getApiErrorMessage(reason, 'Session did not stop.'))
    }
  }

  const discard = async (sampleId: string) => {
    if (!active) return
    setError(null)
    try {
      const session = await api.rejectDatasetSample(active.sessionId, sampleId)
      setLocalSession({ data: session, baseline: sessions.data })
      setNotice({ key: 'Capture discarded. The image file was removed.' })
      sessions.reload()
    } catch (reason) {
      setError(getApiErrorMessage(reason, 'Capture was not discarded.'))
    }
  }

  const labelsReady = Boolean(labels.productClass && labels.qualityLabel && labels.colorLabel && labels.sizeLabel)

  return (
    <div className="flex flex-col gap-4">
      {active?.simulation && <SimulationBanner text={t("The active dataset session is marked SIMULATION.")} />}
      <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,1.4fr)_minmax(320px,0.8fr)]">
        <div className="flex min-h-[420px] flex-col gap-2">
          <CameraStage
            sourceWidth={sourceWidth}
            sourceHeight={sourceHeight}
            roi={recipe?.definition.roi}
            simulation={active?.simulation}
            title={t("Preview frame")}
          />
          <p className="text-xs text-text-muted">{t("This image is the live 240×176 preview. Capture from camera stores one high-quality still on the server. The browser does not read pixels from this preview.")}</p>
        </div>
        <div className="flex flex-col gap-3">
          <div className="grid grid-cols-2 gap-2">
            <StatusPill label={t("Vision")} value={text(diagnostics.data?.vision ?? '…')} tone={healthTone(diagnostics.data?.vision)} />
            <StatusPill label={t("Camera")} value={text(diagnostics.data?.camera ?? '…')} tone={healthTone(diagnostics.data?.camera)} />
            <StatusPill label={t("Session")} value={text(active ? active.status : 'NONE')} />
            <StatusPill label={t("Mode")} value={text(active?.captureMode ?? captureMode)} />
          </div>
          <Panel title={active ? active.recipeName : recipe?.name ?? t('Product recipe')}>
            {active ? (
              <div className="space-y-2 text-sm">
                <p data-testid="dataset-count">{t('{count} captures', { count: active.imageCount })}</p>
                <p>{t("Labels:")}{' '}{active.productClass} / {text(active.qualityLabel)} / {text(active.colorLabel)} / {text(active.sizeLabel)}</p>
                <p className="text-text-muted">{active.labelStatus}</p>
                {active.notes && <p>{t("Notes:")}{' '}{active.notes}</p>}
                {(
                  <button type="button" className="rounded bg-warning px-3 py-2 text-sm text-white" disabled={!apiAvailable} onClick={() => void stop()}>{t("Stop session")}</button>
                )}
              </div>
            ) : (
              <p className="text-sm text-text-muted">{t("No dataset session is active. Choose the recipe labels, then start a session.")}</p>
            )}
          </Panel>
          {(
            <div className="flex flex-col gap-2">
              <button
                type="button"
                data-testid="dataset-capture"
                className="rounded-xl bg-primary px-6 py-4 text-lg font-semibold text-white disabled:opacity-50"
                disabled={!apiAvailable || !active || busy || !labelsReady || !!sessions.error || !!diagnostics.error}
                onClick={() => void captureFromCamera(labels)}
              >
                {busy ? t('Capturing…') : t('Capture from camera')}
              </button>
              {active?.captureMode === 'interval' && (
                <button
                  type="button"
                  className="rounded-lg border border-card-border px-3 py-2 text-sm"
                  onClick={() => {
                    const next = !intervalRunning
                    intervalOn.current = next
                    setIntervalRunning(next)
                  }}
                >
                  {intervalRunning ? t('Stop interval') : t('Start interval ({interval} ms)', { interval: active.intervalMs ?? 0 })}
                </button>
              )}
              <p className="text-xs text-text-muted">{t("Each press stores one high-quality JPEG. If that still fails and preview fallback is disabled, nothing is stored. Interval capture uses the labels currently selected on this page. Hardware trigger stays deferred.")}</p>
            </div>
          )}
        </div>
      </div>

      <Panel title={active ? t('Labels for the next capture') : t('Session labels')}>
        <LabelFields definition={recipe?.definition} labels={labels} onChange={setLabels} />
        {!active && (
          <form className="mt-4 grid grid-cols-1 gap-3 md:grid-cols-3" onSubmit={start}>
            <label className="text-sm">
              <span className="mb-1 block text-text-muted">{t("Recipe")}</span>
              <select className={fieldClass} value={recipeId} onChange={event => setRecipeId(event.target.value)}>
                {(recipes.data ?? []).map(item => (
                  <option key={item.id} value={item.id}>{item.name}{item.isActive ? ` (${t('Active')})` : ''}</option>
                ))}
              </select>
            </label>
            <label className="text-sm">
              <span className="mb-1 block text-text-muted">{t("Capture mode")}</span>
              <select className={fieldClass} value={captureMode} onChange={event => setCaptureMode(event.target.value === 'interval' ? 'interval' : 'manual')}>
                <option value="manual">{t("Manual")}</option>
                <option value="interval">{t("Interval")}</option>
              </select>
            </label>
            {captureMode === 'interval' && (
              <label className="text-sm">
                <span className="mb-1 block text-text-muted">{t("Interval milliseconds")}</span>
                <input className={fieldClass} type="number" min={200} max={60000} value={intervalMs} onChange={event => setIntervalMs(Number(event.target.value))} />
              </label>
            )}
            <label className="flex items-center gap-2 text-sm">
              <input type="checkbox" checked={simulation} onChange={event => setSimulation(event.target.checked)} />{t("Mark SIMULATION")}</label>
            <button className="rounded-lg bg-primary px-3 py-2 text-sm text-white disabled:opacity-50" type="submit" disabled={!apiAvailable || !labelsReady || !recipeId}>{t("Start session")}</button>
          </form>
        )}
      </Panel>

      {active && (
        <Panel title={t("Recent captures")}>
          <RecentCaptures sessionId={active.sessionId} samples={active.recentSamples ?? []} onDiscard={sampleId => void discard(sampleId)} />
        </Panel>
      )}

      {active && (
        <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
          <CountList title={t("Class distribution")} counts={active.classCounts} />
          <CountList title={t("Quality distribution")} counts={active.qualityCounts} />
          <CountList title={t("Defect distribution")} counts={active.defectCounts} />
        </div>
      )}

      <Panel title={t("Sessions")}>
        <ul className="space-y-2 text-sm">
          {(sessions.data ?? []).map(item => (
            <li key={item.sessionId}>
              {date(item.startedAt)} · {text(item.status)} · {t('{count} images', { count: item.imageCount })} ·{' '}{item.productClass} · {text(item.captureMode)}
              {item.simulation && <span className="text-warning-light">{' '}{t("SIMULATION")}</span>}
            </li>
          ))}
          {(sessions.data ?? []).length === 0 && <li className="text-text-muted">{t("No dataset sessions yet.")}</li>}
        </ul>
      </Panel>
      {(sessions.error || diagnostics.error) && <ErrorNote>{sessions.error || diagnostics.error}</ErrorNote>}
      {notice && <p className="text-sm">{message(notice)}</p>}
      {error && <ErrorNote>{error}</ErrorNote>}
    </div>
  )
}

function LabelFields({
  definition,
  labels,
  disabled = false,
  onChange,
}: {
  definition?: RecipeDefinition
  labels: Labels
  disabled?: boolean
  onChange: (labels: Labels) => void
}) {
  const { t } = useLanguage()
  const classes = definition?.productClasses ?? []
  const colors = definition?.allowedColors ?? []
  const sizes = definition?.sizeClasses ?? []
  const defects = definition?.defectClasses ?? []
  const set = (patch: Partial<Labels>) => onChange({ ...labels, ...patch })

  return (
    <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
      <Choice label={t("Product class")} value={labels.productClass} options={classes} disabled={disabled} onChange={productClass => set({ productClass })} />
      <label className="text-sm">
        <span className="mb-1 block text-text-muted">{t("Quality")}</span>
        <select className={fieldClass} value={labels.qualityLabel} disabled={disabled} onChange={event => set({ qualityLabel: event.target.value })}>
          <option value="GOOD">{t("GOOD")}</option>
          <option value="DEFECTIVE">{t("DEFECTIVE")}</option>
        </select>
      </label>
      <Choice label={t("Color")} value={labels.colorLabel} options={colors} disabled={disabled} onChange={colorLabel => set({ colorLabel })} />
      <Choice label={t("Size")} value={labels.sizeLabel} options={sizes} disabled={disabled} onChange={sizeLabel => set({ sizeLabel })} />
      <fieldset className="md:col-span-2">
        <legend className="mb-1 text-sm text-text-muted">{t("Defects")}</legend>
        {defects.length === 0 ? (
          <p className="text-sm text-text-muted">{t("This recipe does not define defect classes.")}</p>
        ) : (
          <div className="flex flex-wrap gap-3">
            {defects.map(item => (
              <label key={item.code} className="flex items-center gap-2 text-sm">
                <input
                  type="checkbox"
                  disabled={disabled}
                  checked={labels.defectLabels.includes(item.code)}
                  onChange={event => {
                    const next = event.target.checked
                      ? [...labels.defectLabels, item.code]
                      : labels.defectLabels.filter(code => code !== item.code)
                    set({ defectLabels: next })
                  }}
                />
                {item.displayName} ({item.code})
              </label>
            ))}
          </div>
        )}
      </fieldset>
      <label className="text-sm md:col-span-2">
        <span className="mb-1 block text-text-muted">{t("Notes")}</span>
        <textarea className={fieldClass} rows={2} value={labels.notes} disabled={disabled} onChange={event => set({ notes: event.target.value })} />
      </label>
    </div>
  )
}

function Choice({
  label,
  value,
  options,
  disabled,
  onChange,
}: {
  label: string
  value: string
  options: { code: string; displayName: string }[]
  disabled: boolean
  onChange: (value: string) => void
}) {
  if (options.length === 0) {
    return (
      <label className="text-sm">
        <span className="mb-1 block text-text-muted">{label}</span>
        <input className={fieldClass} value={value} disabled={disabled} onChange={event => onChange(event.target.value)} />
      </label>
    )
  }

  return (
    <label className="text-sm">
      <span className="mb-1 block text-text-muted">{label}</span>
      <select className={fieldClass} value={value} disabled={disabled} onChange={event => onChange(event.target.value)}>
        {options.map(item => (
          <option key={item.code} value={item.code}>{item.displayName} ({item.code})</option>
        ))}
      </select>
    </label>
  )
}

function CountList({ title, counts }: { title: string; counts?: Record<string, number> }) {
  const { number, t, text } = useLanguage()
  const entries = Object.entries(counts ?? {})
  return (
    <Panel title={title}>
      {entries.length === 0 ? (
        <p className="text-sm text-text-muted">{t("No captures yet.")}</p>
      ) : (
        <ul className="space-y-1 text-sm">
          {entries.map(([name, count]) => <li key={name} className="flex justify-between gap-3"><span>{text(name)}</span><span>{number(count)}</span></li>)}
        </ul>
      )}
    </Panel>
  )
}

function RecentCaptures({
  sessionId,
  samples,
  onDiscard,
}: {
  sessionId: string
  samples: DatasetSample[]
  onDiscard: (sampleId: string) => void
}) {
  const apiAvailable = useApiAvailability()
  const { date, t, text } = useLanguage()
  const signature = samples.map(sample => sample.sampleId).join('|')
  const samplesRef = useRef(samples)
  const [urls, setUrls] = useState<Record<string, string>>({})

  useEffect(() => {
    samplesRef.current = samples
  }, [samples])

  useEffect(() => {
    let cancelled = false
    const created: string[] = []
    const load = async () => {
      const next: Record<string, string> = {}
      for (const sample of samplesRef.current) {
        try {
          const blob = await api.readDatasetImage(sessionId, sample.fileName)
          const url = URL.createObjectURL(blob)
          created.push(url)
          next[sample.sampleId] = url
        } catch {
          next[sample.sampleId] = ''
        }
      }
      if (!cancelled) setUrls(next)
    }
    void load()
    return () => {
      cancelled = true
      for (const url of created) URL.revokeObjectURL(url)
    }
  }, [sessionId, signature])

  if (samples.length === 0) return <p className="text-sm text-text-muted">{t("No captures in this session yet.")}</p>

  return (
    <ul className="grid grid-cols-2 gap-3 lg:grid-cols-4">
      {samples.map(sample => (
        <li key={sample.sampleId} className="overflow-hidden rounded-lg border border-card-border bg-surface">
          {urls[sample.sampleId] ? (
            <img src={urls[sample.sampleId]} alt="" className="h-28 w-full bg-black object-contain" />
          ) : (
            <div className="flex h-28 items-center justify-center bg-black text-xs text-slate-400">{t("Thumbnail pending")}</div>
          )}
          <div className="space-y-1 p-2 text-xs">
            <p className="truncate font-medium">{sample.fileName}</p>
            <p>{date(sample.capturedAt)}</p>
            <p>{sample.productClass} / {text(sample.qualityLabel)} / {text(sample.colorLabel)} / {text(sample.sizeLabel)}</p>
            <p className="text-text-muted">
              {sample.defectLabels.length > 0 ? sample.defectLabels.map(text).join(', ') : t('No defects')}
              {' · '}{text(sample.frameRole)}
              {sample.captureSource ? ` · ${sample.captureSource}` : ''}
              {sample.sourceWidth && sample.sourceHeight ? ` · ${sample.sourceWidth}×${sample.sourceHeight}` : ''}
            </p>
            {sample.previewFallback && <p className="text-warning-light">{t("Preview fallback was used. This is not the high-quality still.")}</p>}
            {(
              <button type="button" className="rounded border border-danger/50 px-2 py-1 text-danger-light" disabled={!apiAvailable} onClick={() => onDiscard(sample.sampleId)}>{t("Discard")}</button>
            )}
          </div>
        </li>
      ))}
    </ul>
  )
}
