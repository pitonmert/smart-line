import { useDashboardState, useDashboardDispatch } from '../../context/DashboardContext'

export default function ChartModeToggle() {
  const { chartMode } = useDashboardState()
  const dispatch = useDashboardDispatch()

  return (
    <div className="inline-flex rounded-lg bg-surface border border-card-border text-xs overflow-hidden">
      <button
        onClick={() => dispatch({ type: 'SET_CHART_MODE', payload: 'live' })}
        className={`px-3 py-1.5 transition-colors ${chartMode === 'live' ? 'bg-primary-light text-white' : 'text-text-muted hover:text-text'}`}
      >
        Canlı
      </button>
      <button
        onClick={() => dispatch({ type: 'SET_CHART_MODE', payload: 'historical' })}
        className={`px-3 py-1.5 transition-colors ${chartMode === 'historical' ? 'bg-primary-light text-white' : 'text-text-muted hover:text-text'}`}
      >
        Geçmiş
      </button>
    </div>
  )
}
