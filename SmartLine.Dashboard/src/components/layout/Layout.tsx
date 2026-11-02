import type { ReactNode } from 'react'
import Header from './Header'
import TopNav from './TopNav'
import type { AppPath } from '../../navigation/routes'

export default function Layout({ children, path, navigate }: { children: ReactNode; path: AppPath | null; navigate: (path: AppPath) => void }) {
  return <div className="app-shell">
    <Header />
    <TopNav path={path} navigate={navigate} />
    <main className="page-content">{children}</main>
  </div>
}
