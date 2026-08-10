import { useDashboardState } from '../../context/DashboardContext'
import GaugeBase from './GaugeBase'

export default function EnvironmentPanel() {
  const { conveyor } = useDashboardState()
  const temp = conveyor?.temperature ?? 0
  const humidity = conveyor?.humidity ?? 0

  return (
    <div className="bg-card rounded-xl border border-card-border p-5 flex flex-col">
      <h2 className="text-base font-semibold mb-4">Ortam</h2>
      <div className="flex items-center justify-around flex-1">
        <GaugeBase
          value={temp}
          min={-40}
          max={80}
          unit="°C"
          label="Sıcaklık"
          thresholds={[
            { value: 35, color: '#f59e0b' },
            { value: 40, color: '#ef4444' },
          ]}
        />
        <div className="w-px self-stretch bg-card-border" />
        <GaugeBase
          value={humidity}
          min={0}
          max={100}
          unit="%"
          label="Nem"
          defaultColor="#06b6d4"
          thresholds={[{ value: 70, color: '#f59e0b' }]}
        />
      </div>
    </div>
  )
}
