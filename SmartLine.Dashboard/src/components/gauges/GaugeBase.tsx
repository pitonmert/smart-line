interface Threshold {
  value: number
  color: string
}

interface Props {
  value: number
  min: number
  max: number
  unit: string
  label: string
  thresholds: Threshold[]
  defaultColor?: string
}

export default function GaugeBase({ value, min, max, unit, label, thresholds, defaultColor = '#3b82f6' }: Props) {
  const range = max - min
  const clamped = Math.min(Math.max(value, min), max)
  const ratio = (clamped - min) / range
  const angle = -135 + ratio * 270

  let color = defaultColor
  for (const t of thresholds) {
    if (value >= t.value) color = t.color
  }

  const r = 44
  const cx = 50
  const cy = 54
  const startAngle = -135
  const endAngle = startAngle + ratio * 270

  const toRad = (deg: number) => (deg * Math.PI) / 180
  const arcPath = (start: number, end: number, radius: number) => {
    const x1 = cx + radius * Math.cos(toRad(start))
    const y1 = cy + radius * Math.sin(toRad(start))
    const x2 = cx + radius * Math.cos(toRad(end))
    const y2 = cy + radius * Math.sin(toRad(end))
    const large = end - start > 180 ? 1 : 0
    return `M ${x1} ${y1} A ${radius} ${radius} 0 ${large} 1 ${x2} ${y2}`
  }

  return (
    <div className="flex flex-col items-center">
      <svg viewBox="0 0 100 80" className="w-full max-w-[180px]">
        <path d={arcPath(-135, 135, r)} fill="none" stroke="#e2e8f0" strokeWidth="6" strokeLinecap="round" />
        {ratio > 0.005 && (
          <path d={arcPath(startAngle, endAngle, r)} fill="none" stroke={color} strokeWidth="6" strokeLinecap="round" />
        )}
        <line
          x1={cx}
          y1={cy}
          x2={cx + 30 * Math.cos(toRad(angle))}
          y2={cy + 30 * Math.sin(toRad(angle))}
          stroke={color}
          strokeWidth="2"
          strokeLinecap="round"
        />
        <circle cx={cx} cy={cy} r="3" fill={color} />
      </svg>
      <div className="text-2xl font-bold tabular-nums -mt-2" style={{ color }}>
        {typeof value === 'number' ? value.toFixed(1) : '—'}{unit}
      </div>
      <div className="text-xs text-text-muted mt-0.5">{label}</div>
    </div>
  )
}
