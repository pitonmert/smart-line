import { useEffect, useState } from 'react'
import { FaCog } from 'react-icons/fa'
import { useDashboardState } from '../../context/DashboardContext'

const connectionLabels: Record<string, { text: string; color: string }> = {
  connecting: { text: 'Bağlanıyor...', color: 'bg-warning-light' },
  connected: { text: 'Canlı', color: 'bg-success-light' },
  disconnected: { text: 'Bağlantı Kesildi', color: 'bg-danger-light' },
  reconnecting: { text: 'Yeniden Bağlanıyor', color: 'bg-warning-light' },
}

export default function Header({ extra }: { extra?: React.ReactNode }) {
  const { connection, lastHeartbeat } = useDashboardState()
  const [now, setNow] = useState(new Date())
  const connInfo = connectionLabels[connection]

  useEffect(() => {
    const id = setInterval(() => setNow(new Date()), 1000)
    return () => clearInterval(id)
  }, [])

  const isOffline = lastHeartbeat && (now.getTime() - lastHeartbeat.getTime()) > 30000

  return (
    <header className="flex items-center justify-between px-6 py-3 bg-card border-b border-card-border">
      <div className="flex items-center gap-3">
        <FaCog className="text-primary-light text-xl" />
        <div>
          <h1 className="text-lg font-semibold tracking-tight">SmartLine</h1>
          <p className="hidden text-[11px] text-text-muted sm:block">Inspection control</p>
        </div>
      </div>

      <div className="flex items-center gap-4 text-sm">
        {extra}
        {lastHeartbeat && (
          <div className="flex items-center gap-2">
            <span className={`w-2 h-2 rounded-full ${isOffline ? 'bg-danger-light' : 'bg-success-light'}`} />
            <span className="text-text-muted">{isOffline ? 'Cihaz Çevrimdışı' : 'Cihaz Çevrimiçi'}</span>
          </div>
        )}

        <div className="flex items-center gap-2">
          <span className={`w-2 h-2 rounded-full ${connInfo.color}`} />
          <span className="text-text-muted">{connInfo.text}</span>
        </div>

        <span className="text-text-muted font-mono">
          {now.toLocaleTimeString('tr-TR')}
        </span>


      </div>
    </header>
  )
}
