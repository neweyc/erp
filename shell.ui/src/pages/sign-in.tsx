import { useState } from 'react'
import { api, ApiError } from '../lib/session'
import { useSession } from '../components/session-provider'

/**
 * Wording per problem code. A single "please sign in again" cannot tell an expired session from
 * a revoked one from an organisation on hold, and each needs a different next step.
 */
const EXPLANATION: Record<string, string> = {
  session_invalid: 'Your session has expired. Please sign in again.',
  session_revoked: 'You were signed out. Sign in again to continue.',
  session_idle_timeout: 'You were signed out after a period of inactivity.',
  role_changed: 'Your permissions changed. Please sign in again.',
  user_deactivated: 'This account is no longer active. Contact your administrator.',
  tenant_retired: 'This organisation’s account has been closed.',
  invalid_credentials: 'That email address and password do not match.',
}

export function SignIn() {
  const { refresh, problemCode } = useSession()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  async function submit(event: React.FormEvent) {
    event.preventDefault()
    setSubmitting(true)
    setError(null)

    try {
      await api('/api/core/v1/auth/sign-in', {
        method: 'POST',
        body: JSON.stringify({ email, password }),
      })
      await refresh()
    } catch (caught) {
      const code = caught instanceof ApiError ? caught.problemCode : 'unknown'
      setError(EXPLANATION[code] ?? 'Sign-in failed. Please try again.')
    } finally {
      setSubmitting(false)
    }
  }

  // Shown only before the first attempt: once someone has tried, the attempt's own result is
  // the relevant message, and leaving the old one up reads as two contradictory errors.
  const reason = !error && problemCode ? EXPLANATION[problemCode] : null

  return (
    <main>
      <h1>Sign in</h1>
      {reason && <p role="status">{reason}</p>}

      <form onSubmit={submit}>
        <label htmlFor="email">Email</label>
        <input
          id="email"
          type="email"
          autoComplete="username"
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          required
        />

        <label htmlFor="password">Password</label>
        <input
          id="password"
          type="password"
          autoComplete="current-password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          required
        />

        {error && <p role="alert">{error}</p>}

        <button type="submit" disabled={submitting}>
          {submitting ? 'Signing in…' : 'Sign in'}
        </button>
      </form>
    </main>
  )
}
