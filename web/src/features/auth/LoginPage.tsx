import { useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import { useAuth } from '../../app/authState'
import { ApiError } from '../../app/apiClient'

export function LoginPage() {
  const { signIn } = useAuth()
  const navigate = useNavigate()

  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    setError(null)
    setBusy(true)

    try {
      await signIn(email, password)
      await navigate('/requests')
    } catch (caught) {
      // Show the server's wording, which is deliberately the same for a wrong password and an
      // unknown email (S8.2). Never show a raw status code (S9.8).
      setError(
        caught instanceof ApiError
          ? caught.message
          : 'Could not reach the server. Check it is running and try again.',
      )
    } finally {
      setBusy(false)
    }
  }

  return (
    <main className="page page--narrow">
      <h1>Sign in</h1>

      <form onSubmit={handleSubmit} noValidate>
        <label htmlFor="email">Email</label>
        <input
          id="email"
          name="email"
          type="email"
          autoComplete="username"
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          required
        />

        <label htmlFor="password">Password</label>
        <input
          id="password"
          name="password"
          type="password"
          autoComplete="current-password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          required
        />

        {error !== null && (
          <p role="alert" className="error">
            {error}
          </p>
        )}

        <button type="submit" disabled={busy}>
          {busy ? 'Signing in…' : 'Sign in'}
        </button>
      </form>

      <p className="hint">
        Signing in again after a page reload is expected: the session is held in memory only.
      </p>
    </main>
  )
}
