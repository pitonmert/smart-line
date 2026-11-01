import { useState, type FormEvent } from 'react'
import { api, getApiErrorMessage } from '../api/client'
import { fieldClass } from '../components/inspection/format'
import { ErrorNote, Panel, SimulationBanner } from '../components/inspection/ui'
import { usePolling } from '../hooks/usePolling'
import type { CalibrationDefinition } from '../types/inspection'

const gates = ['BIN_A', 'BIN_B', 'BIN_C', 'REJECT_BIN', 'MANUAL_REVIEW']

function optionalNumber(value: FormDataEntryValue | null) {
  const text = String(value ?? '').trim()
  return text === '' ? null : Number(text)
}

export default function CalibrationPage() {
  const profiles = usePolling(() => api.getCalibrationProfiles(), 8000)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)

  const save = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    const formElement = event.currentTarget
    const form = new FormData(formElement)
    const red = optionalNumber(form.get('red'))
    const definition: CalibrationDefinition = {
      sourceWidth: optionalNumber(form.get('sourceWidth')),
      sourceHeight: optionalNumber(form.get('sourceHeight')),
      pixelsPerMillimeter: optionalNumber(form.get('pixels')),
      orientationDegrees: Number(form.get('orientation') || 0),
      flipHorizontal: form.get('flipHorizontal') === 'on',
      flipVertical: form.get('flipVertical') === 'on',
      colorReference: red == null ? null : { red, green: Number(form.get('green')), blue: Number(form.get('blue')) },
      beltSpeedMmPerSecond: optionalNumber(form.get('speed')),
      inspectionToSorterDistanceMm: optionalNumber(form.get('distance')),
      cameraRoi: null,
      inspectionZone: null,
      perspectiveReference: null,
      gateMappings: gates.map(binCode => ({ binCode, servoDegrees: 0, enabled: false })),
      tofBaseline: { source: 'NOT_INTEGRATED', productPresent: null, distanceMm: null, triggerTimestamp: null, sensorHealthy: null },
      hardwareActuationEnabled: false,
      hardwareActuationConfirmed: false,
      notes: String(form.get('notes') || ''),
    }
    setError(null)
    try {
      await api.createCalibrationProfile({ name: String(form.get('name')), description: String(form.get('description') || ''), definition })
      setMessage('Calibration profile stored. Gates remain disabled and no VL53L1X reading was saved.')
      profiles.reload()
      formElement.reset()
    } catch (reason) {
      setError(getApiErrorMessage(reason, 'Calibration was rejected.'))
    }
  }

  return (
    <div className="flex flex-col gap-4">
      {profiles.data?.some(item => item.simulationDataPresent) && <SimulationBanner text="A calibration profile contains simulated time-of-flight data." />}
      <p className="text-xs text-text-muted">Scale applies only to the specified still image dimensions. Orientation, flip and perspective are reserved settings; they do not transform images in v0.2.0.</p>
      {profiles.error && <ErrorNote>{profiles.error}</ErrorNote>}
      <Panel title="Profiles">
        <ul className="space-y-2 text-sm">
          {(profiles.data ?? []).map(item => (
            <li key={item.id} className="flex items-center justify-between gap-3 border-b border-card-border py-2">
              <span>{item.name} v{item.version} {item.isActive && <span className="text-success-light">active</span>} {item.simulationDataPresent && <span className="text-warning-light">SIMULATION</span>}</span>
              {!item.isActive && <button type="button" className="rounded bg-primary px-2 py-1 text-xs text-white" onClick={async () => { try { await api.activateCalibrationProfile(item.id); profiles.reload() } catch (reason) { setError(getApiErrorMessage(reason, 'Profile was not activated.')) } }}>Activate</button>}
            </li>
          ))}
          {profiles.data && profiles.data.length === 0 && <li className="text-text-muted">No calibration profile is stored.</li>}
        </ul>
      </Panel>
      {(
        <Panel title="New profile">
          <form className="grid grid-cols-1 gap-3 md:grid-cols-3" onSubmit={save}>
            <input name="name" required placeholder="Name" className={fieldClass} />
            <input name="description" placeholder="Description" className={fieldClass} />
            <select disabled name="orientation" defaultValue="0" className={fieldClass}><option>0</option><option>90</option><option>180</option><option>270</option></select>
            <input name="sourceWidth" type="number" min="1" max="10000" placeholder="Calibration image width px" className={fieldClass} />
            <input name="sourceHeight" type="number" min="1" max="10000" placeholder="Calibration image height px" className={fieldClass} />
            <input name="pixels" type="number" step="0.01" placeholder="Pixels per mm" className={fieldClass} />
            <input name="speed" type="number" step="1" placeholder="Belt mm/s" className={fieldClass} />
            <input name="distance" type="number" step="1" placeholder="Inspection to sorter mm" className={fieldClass} />
            <input name="red" type="number" placeholder="White reference R" className={fieldClass} />
            <input name="green" type="number" placeholder="G" className={fieldClass} />
            <input name="blue" type="number" placeholder="B" className={fieldClass} />
            <label className="flex items-center gap-2 text-sm"><input disabled name="flipHorizontal" type="checkbox" /> Flip horizontal</label>
            <label className="flex items-center gap-2 text-sm"><input disabled name="flipVertical" type="checkbox" /> Flip vertical</label>
            <input name="notes" placeholder="Notes" className={fieldClass} />
            <button type="submit" className="rounded-lg bg-primary px-3 py-2 text-sm text-white">Save profile</button>
          </form>
          <p className="mt-3 text-xs text-text-muted">Speed must be above 0 and at most 5000 mm/s. Distance must be 0 to 20000 mm. Servo gates are stored disabled. Hardware actuation stays off. Time of flight stays NOT_INTEGRATED.</p>
          {message && <p className="mt-3 text-sm">{message}</p>}
          {error && <div className="mt-3"><ErrorNote>{error}</ErrorNote></div>}
        </Panel>
      )}
    </div>
  )
}
