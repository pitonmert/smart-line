import { useLanguage } from '../../i18n/LanguageContext'
import { mainRoutes, type AppPath } from '../../navigation/routes'

export default function TopNav({ path, navigate }: { path: AppPath | null; navigate: (path: AppPath) => void }) {
  const { t } = useLanguage()
  return <nav className="main-nav" aria-label={t('Control center')}>
    {mainRoutes.map(route => <a key={route.path} href={route.path}
      aria-current={(route.path === '/management' ? path?.startsWith('/management') : path === route.path) ? 'page' : undefined}
      onClick={event => { if (!event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey) { event.preventDefault(); navigate(route.path) } }}>
      {t(route.label)}
    </a>)}
  </nav>
}
