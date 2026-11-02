import { useEffect, useState } from 'react'
import { resolvePath, type AppPath } from './routes'

function currentPath() {
  return resolvePath(window.location.pathname)
}

export function useRoute() {
  const [path, setPath] = useState<AppPath | null>(currentPath)
  useEffect(() => {
    const sync = () => {
      const next = currentPath()
      if (next && next !== window.location.pathname) window.history.replaceState(null, '', next + window.location.search)
      setPath(next)
    }
    sync()
    window.addEventListener('popstate', sync)
    return () => window.removeEventListener('popstate', sync)
  }, [])
  const navigate = (next: AppPath) => {
    if (window.location.pathname !== next) window.history.pushState(null, '', next)
    setPath(next)
  }
  return { path, navigate }
}
