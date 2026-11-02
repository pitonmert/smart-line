import { createContext, useContext, useEffect, type ReactNode } from 'react'
import { api } from '../api/client'
import { usePolling } from '../hooks/usePolling'
import { useClock } from '../hooks/useClock'
import { useDashboardState } from './DashboardContext'

function usePlantData() {
  const room = usePolling(() => api.getControlRoom(), 3000)
  const diagnostics = usePolling(() => api.getDiagnostics(), 5000)
  const recipes = usePolling(() => api.getRecipes(), 15000)
  const reloadRoom = room.reload
  const reloadDiagnostics = diagnostics.reload
  useEffect(() => {
    const refresh = () => { reloadRoom(); reloadDiagnostics() }
    window.addEventListener('smartline-inspection', refresh)
    window.addEventListener('smartline-tracking', refresh)
    return () => {
      window.removeEventListener('smartline-inspection', refresh)
      window.removeEventListener('smartline-tracking', refresh)
    }
  }, [reloadRoom, reloadDiagnostics])
  return { room, diagnostics, recipes }
}

const PlantContext = createContext<ReturnType<typeof usePlantData> | null>(null)

export function PlantProvider({ children }: { children: ReactNode }) {
  const data = usePlantData()
  return <PlantContext.Provider value={data}>{children}</PlantContext.Provider>
}

// eslint-disable-next-line react-refresh/only-export-components
export function usePlant() {
  const data = useContext(PlantContext)
  if (!data) throw new Error('PlantProvider is required.')
  return data
}

// eslint-disable-next-line react-refresh/only-export-components
export function useApiAvailability() {
  const { diagnostics, room } = usePlant()
  const { connection } = useDashboardState()
  const now = useClock()
  return connection === 'connected' && !room.error && !diagnostics.error
    && diagnostics.lastSuccessAt !== null && now - diagnostics.lastSuccessAt < 30_000
    && room.lastSuccessAt !== null && now - room.lastSuccessAt < 30_000
}

// eslint-disable-next-line react-refresh/only-export-components
export function useControlAvailability() {
  const available = useApiAvailability()
  const { diagnostics } = usePlant()
  const { conveyor, lastHeartbeat } = useDashboardState()
  const now = useClock()
  const heartbeatFresh = !lastHeartbeat || now - lastHeartbeat.getTime() < 30_000
  return available && !!conveyor && diagnostics.data?.esp32.toLowerCase() === 'online'
    && diagnostics.data?.mqtt.toLowerCase() === 'connected' && heartbeatFresh
}
