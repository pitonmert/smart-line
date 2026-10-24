import { routes, type AppPath } from '../../navigation/routes'

export default function SideNav({ path, onNavigate, compact = false }: { path: AppPath | null; onNavigate: (path: AppPath) => void; compact?: boolean }) {
  return (
    <nav className={compact ? 'flex gap-2 overflow-x-auto px-3 py-2' : 'flex flex-col gap-1 p-3'} aria-label="Control center">
      {routes.map(route => {
        const active = route.path === path
        return (
          <button
            key={route.path}
            type="button"
            onClick={() => onNavigate(route.path)}
            className={`rounded-lg px-3 py-2 text-left text-sm whitespace-nowrap transition-colors ${
              active ? 'bg-primary/20 text-primary-light' : 'text-text-muted hover:bg-card-border/40 hover:text-text'
            }`}
            aria-current={active ? 'page' : undefined}
          >
            {route.label}
          </button>
        )
      })}
    </nav>
  )
}
