import { useEffect } from 'react'
import { api } from '../api/client'
import CameraStage, { type OverlayItem } from '../components/camera/CameraStage'
import ConveyorCard from '../components/conveyor/ConveyorCard'
import EnvironmentPanel from '../components/gauges/EnvironmentPanel'
import SimulationPanel from '../components/inspection/SimulationPanel'
import { decisionTone, formatTime, healthTone } from '../components/inspection/format'
import { ErrorNote, Panel, SimulationBanner, StatusPill } from '../components/inspection/ui'
import { useDashboardState } from '../context/DashboardContext'
import { usePolling } from '../hooks/usePolling'
import type { InspectionResult } from '../types/inspection'

const LIVE_PREVIEW = { width: 240, height: 176 }

function overlayItems(inspection: InspectionResult | null | undefined): OverlayItem[] {
  if (!inspection) return []
  const tone = inspection.decision === 'REJECT' ? 'reject' : inspection.decision === 'MANUAL_REVIEW' ? 'review' : 'pass'
  return inspection.boundingBoxes.map(box => ({
    x: box.x,
    y: box.y,
    width: box.width,
    height: box.height,
    tone,
    label: `${inspection.simulation ? 'SIM ' : ''}${box.label || inspection.productClass} ${Math.round(inspection.confidence * 100)}% ${inspection.decision}`,
  }))
}

export default function ControlRoom() {
  const { conveyor, activeAlerts, connection } = useDashboardState()
  const room = usePolling(() => api.getControlRoom(), 3000)
  const diagnostics = usePolling(() => api.getDiagnostics(), 5000)
  const recipes = usePolling(() => api.getRecipes(), 15000)
  const reloadRoom = room.reload
  const reloadDiagnostics = diagnostics.reload

  useEffect(() => {
    const refresh = () => {
      reloadRoom()
      reloadDiagnostics()
    }
    window.addEventListener('smartline-inspection', refresh)
    window.addEventListener('smartline-tracking', refresh)
    return () => {
      window.removeEventListener('smartline-inspection', refresh)
      window.removeEventListener('smartline-tracking', refresh)
    }
  }, [reloadDiagnostics, reloadRoom])

  const activeRecipe = recipes.data?.find(item => item.isActive)
  const inspection = room.data?.lastInspection
  const sourceWidth = inspection?.geometry?.sourceWidth || activeRecipe?.definition.cameraProfile.sourceWidth || LIVE_PREVIEW.width
  const sourceHeight = inspection?.geometry?.sourceHeight || activeRecipe?.definition.cameraProfile.sourceHeight || LIVE_PREVIEW.height
  const streamUrl = import.meta.env.VITE_CAMERA_STREAM_URL || diagnostics.data?.cameraStreamUrl || activeRecipe?.definition.cameraProfile.streamUrl || 'http://192.168.1.50:81/stream'
  const report = diagnostics.data
  return (
    <div className="flex flex-col gap-4">
      {room.data?.simulationActive && <SimulationBanner text="Simulator values are on screen. Historical rows stay marked even after the latch is cleared." />}
      {(room.error || diagnostics.error) && <ErrorNote>{room.error || diagnostics.error}</ErrorNote>}
      <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,2fr)_minmax(320px,1fr)]">
        <div className="min-h-[420px]">
          <CameraStage
            streamUrl={streamUrl}
            sourceWidth={sourceWidth}
            sourceHeight={sourceHeight}
            items={overlayItems(inspection)}
            roi={activeRecipe?.definition.roi}
            simulation={inspection?.simulation}
            title={inspection ? `${inspection.productClass} · ${inspection.decision} · ${inspection.destinationBin}` : 'Waiting for an inspection'}
          />
        </div>
        <div className="grid grid-cols-2 gap-2 content-start">
          <StatusPill label="API" value={report?.api ?? '…'} tone={healthTone(report?.api)} />
          <StatusPill label="Vision" value={report?.vision ?? '…'} tone={healthTone(report?.vision)} />
          <StatusPill label="AI engine" value={report ? `${report.modelStatus}${report.modelVersion ? ` · ${report.modelVersion}` : ''}` : '…'} tone={healthTone(report?.modelStatus)} />
          <StatusPill label="MQTT" value={report?.mqtt ?? '…'} tone={healthTone(report?.mqtt)} />
          <StatusPill label="ESP32" value={report?.esp32 ?? '…'} tone={healthTone(report?.esp32)} />
          <StatusPill label="SignalR" value={connection} tone={connection === 'connected' ? 'good' : 'warn'} />
          <StatusPill label="Conveyor" value={conveyor ? (conveyor.isRunning ? `Running ${conveyor.speed}` : 'Stopped') : '…'} tone={conveyor?.isEmergencyStopped ? 'bad' : conveyor?.isRunning ? 'good' : 'warn'} />
          <StatusPill label="Fire" value={conveyor?.isFireDetected ? 'Fire detected' : 'Clear'} tone={conveyor?.isFireDetected ? 'bad' : 'good'} />
          <StatusPill label="Temperature" value={conveyor ? `${conveyor.temperature} °C` : '…'} />
          <StatusPill label="Humidity" value={conveyor ? `${conveyor.humidity} %` : '…'} />
          <StatusPill label="Recipe" value={room.data?.activeRecipe ?? 'None'} />
          <StatusPill label="Product" value={room.data?.currentProduct?.publicCode ?? 'None'} />
          <StatusPill label="Last result" value={inspection ? `${inspection.decision} ${Math.round(inspection.confidence * 100)}%` : 'None'} tone={decisionTone(inspection?.decision)} />
          <StatusPill label="Destination" value={inspection?.destinationBin ?? '—'} />
        </div>
      </div>
      <p className="text-xs text-text-muted">Production counts below exclude simulation.</p>
      <div className="grid grid-cols-2 gap-3 md:grid-cols-5">
        <StatusPill label="Total" value={String(room.data?.totalCount ?? 0)} />
        <StatusPill label="Accepted" value={String(room.data?.acceptedCount ?? 0)} tone="good" />
        <StatusPill label="Rejected" value={String(room.data?.rejectedCount ?? 0)} tone="bad" />
        <StatusPill label="Manual review" value={String(room.data?.manualReviewCount ?? 0)} tone="warn" />
        <StatusPill label="Yield" value={room.data?.yieldPercent == null ? '—' : `${room.data.yieldPercent}%`} />
      </div>
      <div className="grid grid-cols-1 gap-4 lg:grid-cols-3">
        <ConveyorCard />
        <div className="lg:col-span-2"><EnvironmentPanel /></div>
      </div>
      <div className="grid grid-cols-1 gap-4 lg:grid-cols-3">
        <Panel title="Bin counts">
          <ul className="space-y-2 text-sm">
            {(room.data?.binCounts ?? []).map(bin => (
              <li key={bin.destinationBin} className="flex justify-between gap-3"><span>{bin.destinationBin}</span><span className="font-mono">{bin.count}</span></li>
            ))}
            {room.data && room.data.binCounts.length === 0 && <li className="text-text-muted">No inspections yet.</li>}
          </ul>
        </Panel>
        <Panel title="Recent events">
          <ul className="max-h-56 space-y-2 overflow-auto text-sm">
            {(room.data?.recentEvents ?? []).map(event => (
              <li key={`${event.createdAt}-${event.eventType}-${event.message}`}>
                <div className="text-text-muted">{formatTime(event.createdAt)} · {event.eventType}</div>
                <div>{event.message}</div>
              </li>
            ))}
          </ul>
        </Panel>
        <Panel title="Active alerts">
          <ul className="space-y-2 text-sm">
            {activeAlerts.filter(alert => alert.isActive).map(alert => (
              <li key={alert.id} className="text-danger-light">{alert.alertType}</li>
            ))}
            {activeAlerts.every(alert => !alert.isActive) && <li className="text-text-muted">No active alerts.</li>}
          </ul>
        </Panel>
      </div>
      <SimulationPanel onChanged={() => { room.reload(); diagnostics.reload() }} />
    </div>
  )
}
