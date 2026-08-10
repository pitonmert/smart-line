import { FaFlask } from 'react-icons/fa'

interface Props {
  isOpen: boolean
  onClick: () => void
}

export default function SimulatorToggle({ isOpen, onClick }: Props) {
  return (
    <button
      onClick={onClick}
      className={`flex items-center gap-2 px-3 py-1.5 rounded-lg text-sm transition-colors ${
        isOpen
          ? 'bg-primary/20 text-primary-light'
          : 'text-text-muted hover:text-text hover:bg-card-border/30'
      }`}
      title="Simülatör Paneli"
    >
      <FaFlask />
      <span className="hidden sm:inline">Simülatör</span>
    </button>
  )
}
