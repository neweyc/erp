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
  it('names the address when the email and password do not match, since a mistyped address answers the same', async () => {
    stubSignIn(403, { problemCode: 'invalid_credentials' })

    await attempt()

    expect(await screen.findByRole('alert')).toHaveTextContent(
      `That email address and password do not match an account at ${window.location.host}. ` +
      'Check them, and that this is the address you were given.')
  })
})
