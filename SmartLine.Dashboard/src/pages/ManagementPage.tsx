import type { AppPath } from '../navigation/routes'
import { managementGroups } from '../navigation/routes'
import { useLanguage } from '../i18n/LanguageContext'
import { useControlAvailability } from '../context/PlantContext'
import ConveyorCard from '../components/conveyor/ConveyorCard'
import RecipesPage from './RecipesPage'
import CalibrationPage from './CalibrationPage'
import SensorsPage from './SensorsPage'
import DiagnosticsPage from './DiagnosticsPage'
import InspectionPage from './InspectionPage'
import SortingPage from './SortingPage'
import DatasetPage from './DatasetPage'
import SimulationPanel from '../components/inspection/SimulationPanel'
import EnvironmentSimulator from '../components/simulator/EnvironmentSimulator'
import FireAlarmSimulator from '../components/simulator/FireAlarmSimulator'
import ProductFlowSimulator from '../components/simulator/ProductFlowSimulator'
import AlertsPanel from '../components/alerts/AlertsPanel'

function SimulationTools() {
  const { t } = useLanguage()
  return <div className="space-y-6"><SimulationPanel />
    {import.meta.env.DEV ? <div className="management-grid"><section className="surface-panel"><EnvironmentSimulator /></section><section className="surface-panel"><FireAlarmSimulator /></section><section className="surface-panel"><ProductFlowSimulator /></section></div>
      : <p className="text-sm text-text-muted">{t('Development tools are unavailable in this build.')}</p>}
  </div>
}

export default function ManagementPage({ path, navigate }: { path: AppPath; navigate: (path: AppPath) => void }) {
  const { t } = useLanguage()
  const controlsAvailable = useControlAvailability()
  const item = managementGroups.flatMap(group => [...group.links]).find(item => item.path === path)
  if (path === '/management') return <div>
    <div className="page-heading"><div><p className="eyebrow">SmartLine</p><h1>{t('Management')}</h1><p>{t('Choose a task')}</p></div></div>
    <div className="management-grid">{managementGroups.map((group, index) => <section className="management-group" key={group.label}>
      <span className="group-number">0{index + 1}</span><h2>{t(group.label)}</h2><p>{t(group.description)}</p>
      <ul>{group.links.map(link => <li key={link.path}><a href={link.path} onClick={event => { if (!event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey) { event.preventDefault(); navigate(link.path) } }}>{t(link.label)}<span aria-hidden="true">↗</span></a></li>)}</ul>
    </section>)}</div>
  </div>
  return <div className="management-page">
    <button className="text-link mb-5" type="button" onClick={() => navigate('/management')}>← {t('Back to management')}</button>
    <div className="page-heading"><h1>{item ? t(item.label) : t('Management')}</h1></div>
    {path === '/management/control' && <>
      {!controlsAvailable && <p role="status" className="connection-banner mb-4">{t('Controls are unavailable until a live device connection is confirmed.')}</p>}
      <div className="control-layout"><ConveyorCard /><AlertsPanel /></div>
    </>}
    {path === '/management/recipes' && <RecipesPage />}
    {path === '/management/calibration' && <CalibrationPage />}
    {path === '/management/sensors' && <SensorsPage />}
    {path === '/management/diagnostics' && <><DiagnosticsPage /><div className="mt-6"><AlertsPanel /></div></>}
    {path === '/management/inspection' && <><p className="tool-note">{t('These tools do not start automatic production inspection.')}</p><InspectionPage /></>}
    {path === '/management/sorting' && <SortingPage />}
    {path === '/management/dataset' && <DatasetPage />}
    {path === '/management/simulation' && <SimulationTools />}
  </div>
}
