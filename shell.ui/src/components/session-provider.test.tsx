import { render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { SessionProvider, useSession } from './session-provider'

function Probe() {
  const { session, loading, problemCode } = useSession()

  if (loading) return <p>loading</p>
  if (!session) return <p>signed out: {problemCode}</p>
  return <p>signed in: {session.email}</p>
}

function renderWith(response: Partial<Response>) {
  vi.stubGlobal('fetch', () => Promise.resolve(response as Response))
  return render(
    <SessionProvider>
      <Probe />
    </SessionProvider>,
  )
}

afterEach(() => vi.unstubAllGlobals())

describe('session provider', () => {
  it('exposes the session once it loads', async () => {
    renderWith({ ok: true, status: 200, json: async () => ({ email: 'ada@acme.test' }) })

    expect(await screen.findByText('signed in: ada@acme.test')).toBeInTheDocument()
  })

  it('keeps the problem code so the sign-in page can explain what happened', async () => {
    renderWith({ ok: false, status: 401, json: async () => ({ problemCode: 'session_idle_timeout' }) })

    // A generic "please sign in" cannot distinguish an expired session from a revoked one from
    // an organisation on hold — and each needs a different next step from the reader.
    expect(await screen.findByText('signed out: session_idle_timeout')).toBeInTheDocument()
  })

  it('fails loudly when used outside its provider', () => {
    // Without the throw, the hook returns undefined and the bug surfaces later as a blank
    // screen somewhere unrelated.
    expect(() => render(<Probe />)).toThrow(/inside a SessionProvider/)
  })
})
