import { useState } from 'react'
import { Alert, Button, Card, Field, Input } from '@app-platform/ui-kit'
import { Brand } from '../components/brand'
import { useSession } from '../components/session'
import { api, ApiError, routes } from '../lib/api'

/**
 * Wording per problem code. Sign-in failures before the password is proved all say the same
 * thing, by design (the server never tells a guesser which half was wrong); after it, the code
 * says what is missing.
 */
const EXPLANATION: Record<string, string> = {
  signed_out: 'You have signed out.',
  session_invalid: 'Your session has ended. Please sign in again.',
  session_revoked: 'You were signed out. Sign in again to continue.',
  session_idle_timeout: 'You were signed out after 30 minutes without activity.',
  operator_deactivated: 'This operator account is no longer active.',
  invalid_credentials: 'That email address and password do not match.',
  mfa_required: 'Enter the 6-digit code from your authenticator app.',
  mfa_code_invalid: 'That code is wrong or has already been used. Wait for the next code and try again.',
  mfa_not_enrolled:
    'This account has no authenticator set up. Reset it on the server: bin/dev operator reset, or reset-platform-user-mfa.',
  rate_limited: 'Too many attempts. Wait a minute and try again.',
}


/**
 * Password and authenticator code in one form, sent together: the platform creates no session
 * until both pass, so there is never a half-signed-in operator.
 */
export function SignIn() {
  const { refresh, problemCode } = useSession()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [code, setCode] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  async function submit(event: React.FormEvent) {
    event.preventDefault()
    setSubmitting(true)
    setError(null)

    try {
      await api(routes.signIn, { method: 'POST', body: JSON.stringify({ email, password, code }) })
      await refresh()
    } catch (caught) {
      const failure = caught instanceof ApiError ? caught.problemCode : 'unknown'
      setError(EXPLANATION[failure] ?? 'Sign-in failed. Please try again.')
      // A code is accepted once, so a failed attempt's code is never worth sending again.
      setCode('')
    } finally {
      setSubmitting(false)
    }
  }

  // Why the last session ended, shown only until the first attempt. Opening the console with no
  // session at all is a 401 with no code, which has no entry here and so says nothing.
  const reason = !error && problemCode ? EXPLANATION[problemCode] : null

  return (
    <div className="flex min-h-svh flex-col items-center justify-center px-4 py-12">
      <main className="w-full max-w-[400px]">
        <div className="mb-8 flex justify-center">
          <Brand className="text-lg" />
        </div>
        <Card className="shadow-sm">
          <div className="grid gap-6 p-6 sm:p-8">
            <div className="grid gap-1.5 text-center">
              <h1 className="text-xl font-semibold tracking-tight">Operator sign-in</h1>
              <p className="text-sm text-muted-foreground">For platform operators. Customers sign in to the app instead.</p>
            </div>

            {reason && <Alert tone="info">{reason}</Alert>}

            <form onSubmit={submit} className="grid gap-4">
              <Field label="Email" htmlFor="email">
                <Input id="email" type="email" autoComplete="username" value={email}
                  onChange={(e) => setEmail(e.target.value)} required />
              </Field>
              <Field label="Password" htmlFor="password">
                <Input id="password" type="password" autoComplete="current-password" value={password}
                  onChange={(e) => setPassword(e.target.value)} required />
              </Field>
              <Field label="Authenticator code" htmlFor="code" hint="The 6-digit code from your authenticator app.">
                <Input id="code" inputMode="numeric" autoComplete="one-time-code" pattern="[0-9]{6}" maxLength={6}
                  value={code} onChange={(e) => setCode(e.target.value.trim())} required />
              </Field>

              {error && <Alert tone="danger">{error}</Alert>}

              <Button type="submit" variant="primary" disabled={submitting} className="mt-1 w-full">
                {submitting ? 'Signing in…' : 'Sign in'}
              </Button>
            </form>
          </div>
        </Card>
      </main>
    </div>
  )
}
