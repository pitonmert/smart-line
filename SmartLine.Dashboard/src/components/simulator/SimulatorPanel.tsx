import { FaTimes } from 'react-icons/fa'
import EnvironmentSimulator from './EnvironmentSimulator'
import FireAlarmSimulator from './FireAlarmSimulator'
import ProductFlowSimulator from './ProductFlowSimulator'

interface Props {
  open: boolean
  onClose: () => void
}

export default function SimulatorPanel({ open, onClose }: Props) {
  return (
    <div
      className={`flex-shrink-0 flex flex-col bg-card border-l border-card-border transition-[width] duration-300 ease-in-out overflow-hidden ${
        open ? 'w-96' : 'w-0'
      }`}
    >
      {/* İç div sabit w-96 — dış div w-0 iken overflow-hidden ile clip'lenir,
          ama React tree mount'lu kalır: auto-flow interval'ları korunur */}
      <div className="w-96 h-full flex flex-col">
        <div className="flex items-center justify-between px-4 py-3 border-b border-card-border">
          <h2 className="text-sm font-semibold text-text">Simülatör Paneli</h2>
          <button
            onClick={onClose}
            className="p-1 rounded text-text-muted hover:text-text hover:bg-card-border/30 transition-colors"
          >
            <FaTimes />
          </button>
        </div>

        <div className="flex-1 overflow-y-auto p-4 flex flex-col gap-5">
          <EnvironmentSimulator />
          <hr className="border-card-border" />
          <FireAlarmSimulator />
          <hr className="border-card-border" />
          <ProductFlowSimulator />
        </div>
      </div>
    </div>
  )
}
