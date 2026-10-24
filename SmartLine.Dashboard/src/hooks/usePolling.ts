import { useCallback, useEffect, useRef, useState } from 'react'
import { getApiErrorMessage } from '../api/client'

export function usePolling<T>(load: () => Promise<T>, intervalMs = 4000, deps: readonly unknown[] = []) {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<string | null>(null)
  const loadRef = useRef(load)
  const [generation, setGeneration] = useState(0)
  const signature = JSON.stringify(deps)

  useEffect(() => {
    loadRef.current = load
  })

  const reload = useCallback(() => setGeneration(value => value + 1), [])

  useEffect(() => {
    let active = true
    let timer: number
    const poll = async () => {
      try {
        const value = await loadRef.current()
        if (active) { setData(value); setError(null) }
      } catch (reason) { if (active) setError(getApiErrorMessage(reason, 'Request failed.')) }
      finally { if (active) timer = window.setTimeout(poll, intervalMs) }
    }
    void poll()
    return () => {
      active = false
      window.clearTimeout(timer)
    }
  }, [generation, signature, intervalMs])

  return { data, error, reload }
}
