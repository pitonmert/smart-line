import { useState } from 'react'
import { DashboardProvider, useDashboardState } from './context/DashboardContext'
import { useSignalR } from './hooks/useSignalR'
import { useDashboard } from './hooks/useDashboard'
import { useAlertSound } from './hooks/useAlertSound'
import Layout from './components/layout/Layout'
import ConveyorCard from './components/conveyor/ConveyorCard'
import EnvironmentPanel from './components/gauges/EnvironmentPanel'
import ProductionAndProducts from './components/charts/ProductionAndProducts'
import FireAlarmOverlay from './components/alerts/FireAlarmOverlay'
import AlertsToggle from './components/alerts/AlertsToggle'
import AlertsPanel from './components/alerts/AlertsPanel'
import LiveLogFeed from './components/activity/LiveLogFeed'
import SimulatorToggle from './components/simulator/SimulatorToggle'
import SimulatorPanel from './components/simulator/SimulatorPanel'
import LoginPage from './components/auth/LoginPage'
import { AuthProvider, useAuth } from './auth/AuthContext'

function Dashboard() {
  useSignalR()
  useDashboard()
  useAlertSound()
  const { error } = useDashboardState()
  const { hasMinimumRole } = useAuth()
  const [openPanel, setOpenPanel] = useState<'simulator' | 'alerts' | null>(null)
  const simulatorEnabled = import.meta.env.DEV && hasMinimumRole('Operator')

  return (
    <Layout
      headerExtra={
        <>
          <AlertsToggle
            isOpen={openPanel === 'alerts'}
            onClick={() => setOpenPanel(p => p === 'alerts' ? null : 'alerts')}
          />
          {simulatorEnabled && (
            <SimulatorToggle
              isOpen={openPanel === 'simulator'}
              onClick={() => setOpenPanel(p => p === 'simulator' ? null : 'simulator')}
            />
          )}
        </>
      }
      sidePanel={
        <>
          <AlertsPanel
            open={openPanel === 'alerts'}
            onClose={() => setOpenPanel(null)}
          />
          {simulatorEnabled && (
            <SimulatorPanel
              open={openPanel === 'simulator'}
              onClose={() => setOpenPanel(null)}
            />
          )}
        </>
      }
    >
      <FireAlarmOverlay />

      {error && (
        <div className="mb-4 p-3 rounded-lg bg-danger/10 border border-danger/30 text-danger-light text-sm">
          {error}
        </div>
      )}

      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4 lg:gap-5">
        {/* Ust sira: Konveyor dar, Ortam 2 sutun */}
        <ConveyorCard />
        <div className="lg:col-span-2">
          <EnvironmentPanel />
        </div>

        {/* Birlesik uretim + son urunler — tam genislik */}
        <div className="md:col-span-2 lg:col-span-3">
          <ProductionAndProducts />
        </div>

        {/* Log akisi — tam genislik */}
        <div className="md:col-span-2 lg:col-span-3">
          <LiveLogFeed />
        </div>
      </div>
    </Layout>
  )
}

function AuthenticatedApp() {
  const { status } = useAuth()

  if (status === 'loading') {
    return (
      <main className="min-h-screen grid place-items-center bg-surface text-sm text-text-muted">
        Oturum doğrulanıyor...
      </main>
    )
  }

  if (status === 'anonymous') return <LoginPage />

  return (
    <DashboardProvider>
      <Dashboard />
    </DashboardProvider>
  )
}

export default function App() {
  return (
    <AuthProvider>
      <AuthenticatedApp />
    </AuthProvider>
  )
}
