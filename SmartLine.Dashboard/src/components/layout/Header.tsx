import { useEffect, useState } from 'react'
import { useLanguage } from '../../i18n/LanguageContext'
import { useTheme } from '../../theme/ThemeContext'
import { usePlant } from '../../context/PlantContext'
import { useDashboardState } from '../../context/DashboardContext'
import { useClock } from '../../hooks/useClock'

export default function Header() {
  const { language, setLanguage, t, text, time } = useLanguage()
  const { theme, setTheme } = useTheme()
  const { room } = usePlant()
  const { conveyor, connection } = useDashboardState()
  const now = useClock()
  const [fullscreenError, setFullscreenError] = useState(false)
  const [isFullscreen, setIsFullscreen] = useState(false)
  useEffect(() => {
    const update = () => setIsFullscreen(!!document.fullscreenElement)
    document.addEventListener('fullscreenchange', update)
    return () => document.removeEventListener('fullscreenchange', update)
  }, [])
  const line = !conveyor ? 'Line status unknown' : conveyor.isEmergencyStopped ? 'Emergency latch active' : conveyor.isRunning ? 'Line running' : 'Line stopped'
  const fullscreen = async () => {
    try {
      if (document.fullscreenElement) await document.exitFullscreen()
      else await document.documentElement.requestFullscreen()
      setFullscreenError(false)
    } catch { setFullscreenError(true) }
  }
  return <header className="app-header">
    <div className="brand"><span className="brand-mark" aria-hidden="true">S</span><strong>SmartLine</strong><span className="brand-caption">{t('Production monitoring')}</span></div>
    <div className="line-summary">
      <span className={conveyor?.isEmergencyStopped ? 'text-danger-light' : 'text-text'}>{t(line)}</span>
      <span className="text-text-muted">{t('Recipe')}: <span className="text-text">{room.data?.activeRecipe ?? '—'}</span></span>
      <span className={connection === 'connected' && !room.error && room.lastSuccessAt !== null && now - room.lastSuccessAt < 30_000 ? 'text-success-light' : 'text-warning-light'}>{text(connection === 'connected' && !room.error && room.lastSuccessAt !== null && now - room.lastSuccessAt < 30_000 ? 'Live' : connection === 'connected' ? 'Unavailable' : connection)}</span>
    </div>
    <div className="header-utilities">
      <div role="group" aria-label={t('Language')} className="segmented">
        {(['tr', 'en'] as const).map(value => <button key={value} type="button" lang={value}
          aria-label={value === 'tr' ? 'Türkçe' : 'English'} aria-pressed={language === value} onClick={() => setLanguage(value)}>{value.toUpperCase()}</button>)}
      </div>
      <div role="group" aria-label={t('Theme')} className="segmented">
        {(['dark', 'light'] as const).map(value => <button key={value} type="button" aria-pressed={theme === value}
          onClick={() => setTheme(value)}>{t(value === 'dark' ? 'Dark' : 'Light')}</button>)}
      </div>
      <button type="button" className="utility-button" onClick={() => void fullscreen()} title={t(isFullscreen ? 'Exit full screen' : 'Full screen')} aria-label={t(isFullscreen ? 'Exit full screen' : 'Full screen')}>⛶</button>
      <time className="header-clock">{time(new Date(now).toISOString())}</time>
    </div>
    {fullscreenError && <p role="status" className="text-xs text-warning-light">{t('Full screen is unavailable.')}</p>}
  </header>
}
