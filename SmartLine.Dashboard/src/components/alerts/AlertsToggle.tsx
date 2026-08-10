import { FaBell } from 'react-icons/fa'
import { useDashboardState } from '../../context/DashboardContext'

interface Props {
  isOpen: boolean
  onClick: () => void
}

export default function AlertsToggle({ isOpen, onClick }: Props) {
  const { activeAlerts } = useDashboardState()
  const count = activeAlerts.filter(a => a.alertType !== 'FIRE').length

  return (
    <button
      onClick={onClick}
      className={`relative flex items-center gap-2 px-3 py-1.5 rounded-lg text-sm transition-colors ${
        isOpen
          ? 'bg-warning/20 text-warning-light'
          : 'text-text-muted hover:text-text hover:bg-card-border/30'
      }`}
      title="Alarmlar"
    >
      <FaBell />
      <span className="hidden sm:inline">Alarmlar</span>
      {count > 0 && (
        <span className="absolute -top-1 -right-1 w-4 h-4 rounded-full bg-danger text-white text-[10px] flex items-center justify-center font-bold">
          {count}
        </span>
      )}
    </button>
  )
}
