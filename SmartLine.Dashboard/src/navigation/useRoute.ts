import { useEffect, useState } from 'react'
import { routes, type AppPath } from './routes'

function currentPath(): AppPath | null {
  const match = routes.find(route => route.path === window.location.pathname)
  return match?.path ?? null
}

export function useRoute() {
  const [path, setPath] = useState<AppPath | null>(currentPath)

  useEffect(() => {
    const onPop = () => setPath(currentPath())
    window.addEventListener('popstate', onPop)
    return () => window.removeEventListener('popstate', onPop)
  }, [])

  const navigate = (next: AppPath) => {
    if (window.location.pathname !== next) {
      window.history.pushState(null, '', next)
    }
    setPath(next)
  }

  return { path, navigate }
}
