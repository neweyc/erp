import { useState } from 'react'
import { api, ApiError } from '../lib/session'

/** Must match the server's rule; the server is authoritative and re-checks it. */
const MINIMUM_PASSWORD_LENGTH = 12

const EXPLANATION: Record<string, string> = {
  invalid_token: 'This invitation link is not valid. It may have expired or already been used.',
  weak_password: `Choose a password of at least ${MINIMUM_PASSWORD_LENGTH} characters.`,
}

/**
 * Turns an invitation into a usable account.
 *
 * Reachable while signed out, because the person has no account yet — the token in the URL is
 * what authenticates them.
 */
export function AcceptInvite() {
  const token = new URLSearchParams(window.location.search).get('token') ?? ''
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [accepted, setAccepted] = useState(false)
  const [submitting, setSubmitting] = useState(false)

  async function submit(event: React.FormEvent) {
    event.preventDefault()
    setSubmitting(true)
    setError(null)

    try {
      await api('/api/core/v1/auth/accept-invite', {
        method: 'POST',
        body: JSON.stringify({ token, password }),
      })
      setAccepted(true)
    } catch (caught) {
      const code = caught instanceof ApiError ? caught.problemCode : 'unknown'
      setError(EXPLANATION[code] ?? 'Could not accept this invitation. Please try again.')
    } finally {
      setSubmitting(false)
    }
  }

  if (accepted) {
    return (
      <main>
        <h1>Invitation accepted</h1>
        {/*
          Deliberately not signed in automatically. Accepting proves the person holds the link;
          signing in proves they know the password they just chose, which is what every later
          session is authenticated against.
        */}
        <p>Your password is set. You can now sign in.</p>
        <a href="/">Go to sign in</a>
      </main>
    )
  }

  if (!token) {
    return (
      <main>
        <h1>Invitation</h1>
        <p role="alert">This invitation link is missing its token. Use the link from your email.</p>
      </main>
    )
  }

  return (
    <main>
      <h1>Accept your invitation</h1>
      <p>Choose a password to finish setting up your account.</p>

      <form onSubmit={submit}>
        <label htmlFor="password">Password</label>
        <input
          id="password"
          type="password"
          autoComplete="new-password"
          minLength={MINIMUM_PASSWORD_LENGTH}
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          required
        />

        {error && <p role="alert">{error}</p>}

        <button type="submit" disabled={submitting}>
          {submitting ? 'Setting your password…' : 'Accept invitation'}
        </button>
      </form>
    </main>
  )
}
