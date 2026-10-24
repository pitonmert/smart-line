import type { ReactNode } from 'react'
import Header from './Header'

export default function Layout({ children, headerExtra, sidePanel, nav, mobileNav }: {
  children: ReactNode
  headerExtra?: ReactNode
  sidePanel?: ReactNode
  nav?: ReactNode
  mobileNav?: ReactNode
}) {
  return (
    <div className="flex h-screen flex-col overflow-hidden">
      <Header extra={headerExtra} />
      <div className="flex flex-1 overflow-hidden">
        {nav && <aside className="hidden w-56 shrink-0 overflow-y-auto border-r border-card-border bg-card lg:block">{nav}</aside>}
        <div className="flex min-w-0 flex-1 flex-col overflow-hidden">
          {mobileNav && <div className="border-b border-card-border bg-card lg:hidden">{mobileNav}</div>}
          <div className="flex flex-1 overflow-hidden">
            <main className="flex-1 overflow-y-auto p-4 lg:p-6">{children}</main>
            {sidePanel}
          </div>
        </div>
      </div>
    </div>
  )
}
