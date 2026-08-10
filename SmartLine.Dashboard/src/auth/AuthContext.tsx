// @refresh reset
import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react'
import { api } from '../api/client'
import type { AuthSession, AuthUser, UserRole } from '../types'

type AuthStatus = 'loading' | 'authenticated' | 'anonymous'

interface AuthContextValue {
  status: AuthStatus
  user: AuthUser | null
  login: (email: string, password: string) => Promise<void>
  logout: () => Promise<void>
  hasMinimumRole: (role: UserRole) => boolean
}

const roleRank: Record<UserRole, number> = {
  Viewer: 1,
  Operator: 2,
  Admin: 3,
}

const AuthContext = createContext<AuthContextValue | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<AuthStatus>('loading')
  const [user, setUser] = useState<AuthUser | null>(null)

  useEffect(() => {
    const applySession = (value: AuthSession | null) => {
      setUser(value?.user ?? null)
      setStatus(value ? 'authenticated' : 'anonymous')
    }

    const unsubscribe = api.subscribeSession(applySession)
    void api.bootstrapSession().then(applySession)
    return unsubscribe
  }, [])

  const login = useCallback(async (email: string, password: string) => {
    await api.login(email, password)
  }, [])

  const logout = useCallback(async () => {
    await api.logout()
  }, [])

  const hasMinimumRole = useCallback((required: UserRole) => {
    const requiredRank = roleRank[required]
    return user?.roles.some(role => roleRank[role] >= requiredRank) ?? false
  }, [user])

  const value = useMemo<AuthContextValue>(() => ({
    status,
    user,
    login,
    logout,
    hasMinimumRole,
  }), [hasMinimumRole, login, logout, status, user])

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

// eslint-disable-next-line react-refresh/only-export-components
export function useAuth() {
  const value = useContext(AuthContext)
  if (!value) throw new Error('useAuth must be used inside AuthProvider')
  return value
}
