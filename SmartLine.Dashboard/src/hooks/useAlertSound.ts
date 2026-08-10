import { useEffect, useRef } from 'react'
import { useDashboardState } from '../context/DashboardContext'

export function useAlertSound() {
  const { activeAlerts } = useDashboardState()
  const prevCountRef = useRef(activeAlerts.length)
  const audioCtxRef = useRef<AudioContext | null>(null)

  useEffect(() => {
    const hasCritical = activeAlerts.some(
      a => a.alertType === 'FIRE' || a.alertType === 'EMERGENCY_STOP'
    )
    const isNew = activeAlerts.length > prevCountRef.current
    prevCountRef.current = activeAlerts.length

    if (hasCritical && isNew) {
      try {
        if (!audioCtxRef.current) audioCtxRef.current = new AudioContext()
        const ctx = audioCtxRef.current
        ctx.resume().then(() => {
          const osc = ctx.createOscillator()
          const gain = ctx.createGain()
          osc.connect(gain)
          gain.connect(ctx.destination)
          osc.frequency.value = 880
          osc.type = 'square'
          gain.gain.value = 0.15
          osc.start()
          osc.stop(ctx.currentTime + 0.3)
        })
      } catch { /* ses destegi yoksa sessiz kal */ }
    }
  }, [activeAlerts])
}
