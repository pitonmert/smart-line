import { useState, type FormEvent } from 'react'
import { FaCog, FaSignInAlt } from 'react-icons/fa'
import { getApiErrorMessage } from '../../api/client'
import { useAuth } from '../../auth/AuthContext'

export default function LoginPage() {
  const { login } = useAuth()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault()
    setLoading(true)
    setError(null)

    try {
      await login(email.trim(), password)
    } catch (loginError) {
      setError(getApiErrorMessage(loginError, 'Giriş yapılamadı.'))
    } finally {
      setLoading(false)
    }
  }

  return (
    <main className="min-h-screen grid place-items-center bg-surface p-4">
      <section className="w-full max-w-sm rounded-2xl border border-card-border bg-card p-7 shadow-xl shadow-black/40">
        <div className="mb-6 flex flex-col items-center gap-2 text-center">
          <div className="grid h-12 w-12 place-items-center rounded-xl bg-primary/10 text-primary-light">
            <FaCog className="text-2xl" />
          </div>
          <h1 className="text-xl font-semibold">SmartLine Dashboard</h1>
          <p className="text-sm text-text-muted">Devam etmek için hesabınızla giriş yapın.</p>
        </div>

        <form className="flex flex-col gap-4" onSubmit={handleSubmit}>
          <label className="flex flex-col gap-1.5 text-sm">
            <span className="font-medium">E-posta</span>
            <input
              autoComplete="username"
              autoFocus
              type="email"
              required
              value={email}
              onChange={event => setEmail(event.target.value)}
              className="rounded-lg border border-card-border bg-surface px-3 py-2.5 outline-none transition focus:border-primary-light focus:ring-2 focus:ring-primary/15"
            />
          </label>

          <label className="flex flex-col gap-1.5 text-sm">
            <span className="font-medium">Parola</span>
            <input
              autoComplete="current-password"
              type="password"
              required
              value={password}
              onChange={event => setPassword(event.target.value)}
              className="rounded-lg border border-card-border bg-surface px-3 py-2.5 outline-none transition focus:border-primary-light focus:ring-2 focus:ring-primary/15"
            />
          </label>

          {error && (
            <p role="alert" className="rounded-lg border border-danger/30 bg-danger/10 p-3 text-sm text-danger-light">
              {error}
            </p>
          )}

          <button
            type="submit"
            disabled={loading || !email.trim() || !password}
            className="mt-1 flex items-center justify-center gap-2 rounded-lg bg-primary px-4 py-2.5 text-sm font-medium text-white transition-colors hover:bg-primary-light disabled:cursor-not-allowed disabled:opacity-50"
          >
            <FaSignInAlt />
            {loading ? 'Giriş yapılıyor...' : 'Giriş yap'}
          </button>
        </form>
      </section>
    </main>
  )
}
