import { useLanguage } from './i18n/LanguageContext'
import { DashboardProvider, useDashboardState } from './context/DashboardContext'
import { PlantProvider } from './context/PlantContext'
import { useSignalR } from './hooks/useSignalR'
import { useDashboard } from './hooks/useDashboard'
import { useAlertSound } from './hooks/useAlertSound'
import Layout from './components/layout/Layout'
import FireAlarmOverlay from './components/alerts/FireAlarmOverlay'
import { useRoute } from './navigation/useRoute'
import ControlRoom from './pages/ControlRoom'
import HistoryPage from './pages/HistoryPage'
import ManagementPage from './pages/ManagementPage'

function Dashboard() {
  const { t, text } = useLanguage()
  useSignalR()
  useDashboard()
  useAlertSound()
  const { error } = useDashboardState()
  const { path, navigate } = useRoute()
  return <Layout path={path} navigate={navigate}>
    {import.meta.env.MODE === 'mock' && <div role="status" className="mock-banner">{t('MOCK PREVIEW · All values and images are examples. No hardware or backend is connected. Actions are read-only.')}</div>}
    <FireAlarmOverlay readOnly={!path?.startsWith('/management')} />
    {error && <div role="alert" className="connection-banner mb-4">{text(error)}</div>}
    {path === '/' && <ControlRoom navigate={navigate} />}
    {path === '/history' && <HistoryPage />}
    {path?.startsWith('/management') && <ManagementPage path={path} navigate={navigate} />}
    {path === null && <div className="surface-panel"><p>{t('This address is not a control-center page.')}</p><button type="button" className="text-link mt-3" onClick={() => navigate('/')}>{t('Production')}</button></div>}
  </Layout>
}

export default function App() {
  return <DashboardProvider><PlantProvider><Dashboard /></PlantProvider></DashboardProvider>
}
