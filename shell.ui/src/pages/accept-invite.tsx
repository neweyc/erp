import { useState } from 'react'
import { Link } from 'react-router-dom'
import { CircleCheck } from 'lucide-react'
import { Alert, Button, Field, Input } from '@app-platform/ui-kit'
import { api, ApiError } from '../lib/session'
import { AuthLayout } from '../components/auth-layout'

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
      <AuthLayout title="Invitation accepted" description="Your password is set. You can now sign in.">
        {/*
          Deliberately not signed in automatically. Accepting proves the person holds the link;
          signing in proves they know the password they just chose, which is what every later
          session is authenticated against.
        */}
        <div className="flex justify-center">
          <CircleCheck className="size-10 text-success" />
        </div>
        <Button asChild variant="primary" className="w-full">
          <Link to="/">Go to sign in</Link>
        </Button>
      </AuthLayout>
    )
  }

  if (!token) {
    return (
      <AuthLayout title="Invitation">
        <Alert tone="danger">This invitation link is missing its token. Use the link from your email.</Alert>
      </AuthLayout>
    )
  }

  return (
    <AuthLayout title="Accept your invitation" description="Choose a password to finish setting up your account.">
      <form onSubmit={submit} className="grid gap-4">
        <Field label="Password" htmlFor="password" hint={`At least ${MINIMUM_PASSWORD_LENGTH} characters.`}>
          <Input
            id="password"
            type="password"
            autoComplete="new-password"
            minLength={MINIMUM_PASSWORD_LENGTH}
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            required
          />
        </Field>

        {error && <Alert tone="danger">{error}</Alert>}

        <Button type="submit" variant="primary" disabled={submitting} className="mt-1 w-full">
          {submitting ? 'Setting your password…' : 'Accept invitation'}
        </Button>
      </form>
    </AuthLayout>
  )
}
