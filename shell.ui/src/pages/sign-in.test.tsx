import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { SessionProvider } from '../components/session-provider'
import { SignIn } from './sign-in'

function stubSignIn(status: number, body: unknown) {
  vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL) =>
    Promise.resolve(String(input).endsWith('/auth/sign-in')
      ? { ok: status < 400, status, json: async () => body } as Response
      : { ok: false, status: 401, json: async () => ({ problemCode: 'not_signed_in' }) } as Response)))
}

async function attempt() {
  render(<SessionProvider><SignIn /></SessionProvider>)
  const user = userEvent.setup()
  await user.type(await screen.findByLabelText('Email'), 'ada@acme.test')
  await user.type(screen.getByLabelText('Password'), 'correct horse')
  await user.click(screen.getByRole('button', { name: /Sign in/ }))
}

afterEach(() => vi.unstubAllGlobals())

describe('signing in', () => {
  it('says when no organization signs in at this address, rather than blaming the password', async () => {
    stubSignIn(404, { problemCode: 'tenant_not_found' })

    await attempt()

    expect(await screen.findByRole('alert')).toHaveTextContent(
      `No organization signs in at ${window.location.host}. Check the address you were given.`)
  })

  it('says when the email and password do not match', async () => {
    stubSignIn(403, { problemCode: 'invalid_credentials' })

    await attempt()

    expect(await screen.findByRole('alert')).toHaveTextContent('That email address and password do not match.')
  })
})
