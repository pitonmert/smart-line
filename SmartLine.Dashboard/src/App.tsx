import { useState } from 'react'
import { DashboardProvider, useDashboardState } from './context/DashboardContext'
import { useSignalR } from './hooks/useSignalR'
import { useDashboard } from './hooks/useDashboard'
import { useAlertSound } from './hooks/useAlertSound'
import Layout from './components/layout/Layout'
import SideNav from './components/layout/SideNav'
import FireAlarmOverlay from './components/alerts/FireAlarmOverlay'
import AlertsToggle from './components/alerts/AlertsToggle'
import AlertsPanel from './components/alerts/AlertsPanel'
import SimulatorToggle from './components/simulator/SimulatorToggle'
import SimulatorPanel from './components/simulator/SimulatorPanel'
import { useRoute } from './navigation/useRoute'
import ControlRoom from './pages/ControlRoom'
import InspectionPage from './pages/InspectionPage'
import AnalyticsPage from './pages/AnalyticsPage'
import SensorsPage from './pages/SensorsPage'
import SortingPage from './pages/SortingPage'
import RecipesPage from './pages/RecipesPage'
import CalibrationPage from './pages/CalibrationPage'
import DatasetPage from './pages/DatasetPage'
import HistoryPage from './pages/HistoryPage'
import DiagnosticsPage from './pages/DiagnosticsPage'

function Dashboard() {
  useSignalR()
  useDashboard()
  useAlertSound()
  const { error } = useDashboardState()
  const [openPanel, setOpenPanel] = useState<'simulator' | 'alerts' | null>(null)
  const simulatorEnabled = import.meta.env.DEV
  const { path, navigate } = useRoute()
  const navigation = <SideNav path={path} onNavigate={navigate} />
  const mobileNavigation = <SideNav compact path={path} onNavigate={navigate} />

  return (
    <Layout
      nav={navigation}
      mobileNav={mobileNavigation}
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

      {path === '/' && <ControlRoom />}
      {path === '/inspection' && <InspectionPage />}
      {path === '/analytics' && <AnalyticsPage />}
      {path === '/sensors' && <SensorsPage />}
      {path === '/sorting' && <SortingPage />}
      {path === '/recipes' && <RecipesPage />}
      {path === '/calibration' && <CalibrationPage />}
      {path === '/dataset' && <DatasetPage />}
      {path === '/history' && <HistoryPage />}
      {path === '/diagnostics' && <DiagnosticsPage />}
      {path === null && (
        <div className="rounded-xl border border-card-border bg-card p-6">
          <p className="text-sm">This address is not a control-center page.</p>
          <button type="button" className="mt-3 rounded-lg bg-primary px-3 py-2 text-sm text-white" onClick={() => navigate('/')}>Control Room</button>
        </div>
      )}
    </Layout>
  )
}

export default function App() {
  return (
    <DashboardProvider>
      <Dashboard />
    </DashboardProvider>
  )
}
