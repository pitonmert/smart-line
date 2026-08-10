import type { ReactNode } from 'react'
import Header from './Header'

export default function Layout({ children, headerExtra, sidePanel }: {
  children: ReactNode
  headerExtra?: ReactNode
  sidePanel?: ReactNode
}) {
  return (
    <div className="h-screen flex flex-col overflow-hidden">
      <Header extra={headerExtra} />
      <div className="flex flex-1 overflow-hidden">
        <main className="flex-1 overflow-y-auto p-4 lg:p-6">
          {children}
        </main>
        {sidePanel}
      </div>
    </div>
  )
}
