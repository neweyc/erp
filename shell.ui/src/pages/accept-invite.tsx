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
 * Where the person signs in, from the API's answer: the organization's address now. The link they
 * followed was made when they were invited, and the address may have changed since (it still works,
 * because the token says which organization it is for). Null when it is this page's own address,
 * so the ordinary in-app link is used.
 */
function elsewhere(signInUrl: string | undefined): string | null {
  if (!signInUrl) return null
  try {
    const url = new URL(signInUrl)
    if (url.protocol !== 'https:' && url.protocol !== 'http:') return null
    return url.origin === window.location.origin ? null : url.origin
  } catch {
    return null
  }
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
  const [signInAt, setSignInAt] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  async function submit(event: React.FormEvent) {
    event.preventDefault()
    setSubmitting(true)
    setError(null)

    try {
      const result = await api<{ signInUrl?: string }>('/api/core/v1/auth/accept-invite', {
        method: 'POST',
        body: JSON.stringify({ token, password }),
      })
      setSignInAt(elsewhere(result?.signInUrl))
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
      <AuthLayout
        title="Invitation accepted"
        description={signInAt
          ? `Your password is set. Your organization signs in at ${signInAt}.`
          : 'Your password is set. You can now sign in.'}
      >
        {/*
          Deliberately not signed in automatically. Accepting proves the person holds the link;
          signing in proves they know the password they just chose, which is what every later
          session is authenticated against.
        */}
        <div className="flex justify-center">
          <CircleCheck className="size-10 text-success" />
        </div>
        <Button asChild variant="primary" className="w-full">
          {signInAt ? <a href={signInAt}>Go to sign in</a> : <Link to="/">Go to sign in</Link>}
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
