export function formatTime(value?: string | null) {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString()
}

export function decisionTone(decision?: string | null): 'good' | 'bad' | 'warn' | 'neutral' {
  if (decision === 'PASS') return 'good'
  if (decision === 'REJECT') return 'bad'
  if (decision === 'MANUAL_REVIEW') return 'warn'
  return 'neutral'
}

const healthyStates = new Set(['healthy', 'connected', 'online'])
const pendingStates = new Set(['stale', 'not_configured', 'not_loaded', 'unknown', 'registered', 'checking', 'connecting', 'reconnecting'])

export function healthTone(value?: string | null): 'good' | 'bad' | 'warn' | 'neutral' {
  if (!value) return 'neutral'
  const normalized = value.toLowerCase()
  if (healthyStates.has(normalized)) return 'good'
  if (pendingStates.has(normalized)) return 'warn'
  return 'bad'
}

export const fieldClass = 'w-full rounded-lg border border-card-border bg-surface px-3 py-2 text-sm outline-none focus:border-primary-light'
