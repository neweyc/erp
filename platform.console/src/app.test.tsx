import { render, renderHook, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { App } from './App'
import { useSession } from './components/session'
import { sent, stubApi } from './test-support'

afterEach(() => {
  vi.unstubAllGlobals()
  window.history.pushState({}, '', '/')
})

const signedOut = { 'GET /api/platform/v1/auth/session': () => ({ status: 401 }) }
const tenants = { 'GET /api/platform/v1/tenants': () => ({ body: [] }) }

async function signIn(code = '123456') {
  const user = userEvent.setup()
  await user.type(await screen.findByLabelText('Email'), 'op@platform.test')
  await user.type(screen.getByLabelText('Password'), 'correct horse')
  await user.type(screen.getByLabelText('Authenticator code'), code)
  await user.click(screen.getByRole('button', { name: 'Sign in' }))
  return user
}

describe('signing in', () => {
  it('asks for the password and the authenticator code together, then opens the tenants', async () => {
    let signedIn = false
    const fetchMock = stubApi({
      ...tenants,
      'GET /api/platform/v1/auth/session': () => (signedIn ? { body: { email: 'op@platform.test' } } : { status: 401 }),
      'POST /api/platform/v1/auth/sign-in': () => {
        signedIn = true
        return { body: { signedIn: true } }
      },
    })
    render(<App />)

    // Opening the console with no session says nothing about why: there was no session to end.
    expect(await screen.findByRole('heading', { name: 'Operator sign-in' })).toBeInTheDocument()
    expect(screen.queryByRole('status')).not.toBeInTheDocument()

    await signIn()

    expect(await screen.findByRole('heading', { name: 'Tenants' })).toBeInTheDocument()
    expect(screen.getByText('op@platform.test')).toBeInTheDocument()
    expect(sent(fetchMock, 'POST', '/api/platform/v1/auth/sign-in')).toEqual([
      { email: 'op@platform.test', password: 'correct horse', code: '123456' },
    ])
  })

  it('explains a refused code, and clears it because a code is never worth sending twice', async () => {
    stubApi({
      ...signedOut,
      'POST /api/platform/v1/auth/sign-in': () => ({ status: 403, body: { problemCode: 'mfa_code_invalid' } }),
    })
    render(<App />)

    await signIn()

    expect(await screen.findByRole('alert')).toHaveTextContent('wrong or has already been used')
    expect(screen.getByLabelText('Authenticator code')).toHaveValue('')
  })

  it('falls back to a general message when the failure has no explanation', async () => {
    stubApi({ ...signedOut, 'POST /api/platform/v1/auth/sign-in': () => ({ status: 500 }) })
    render(<App />)

    await signIn()

    expect(await screen.findByRole('alert')).toHaveTextContent('Sign-in failed. Please try again.')
  })

  it('says why the last session ended', async () => {
    stubApi({ 'GET /api/platform/v1/auth/session': () => ({ status: 401, body: { problemCode: 'session_idle_timeout' } }) })
    render(<App />)

    expect(await screen.findByText(/after 30 minutes without activity/)).toBeInTheDocument()
  })
})

describe('signed in', () => {
  it('sends an unknown address to the tenants', async () => {
    window.history.pushState({}, '', '/nowhere')
    stubApi(tenants)
    render(<App />)

    expect(await screen.findByRole('heading', { name: 'Tenants' })).toBeInTheDocument()
    expect(window.location.pathname).toBe('/tenants')
  })

  it('moves between the three sections', async () => {
    stubApi({
      ...tenants,
      'GET /api/platform/v1/errors': () => ({ body: [] }),
      'GET /api/platform/v1/audit': () => ({ body: [] }),
    })
    render(<App />)
    const user = userEvent.setup()

    await user.click(await screen.findByRole('link', { name: 'Errors' }))
    expect(await screen.findByRole('heading', { name: 'Errors' })).toBeInTheDocument()

    await user.click(screen.getByRole('link', { name: 'Audit log' }))
    expect(await screen.findByRole('heading', { name: 'Audit log' })).toBeInTheDocument()
  })

  it('signs out on the server and returns to sign-in', async () => {
    const fetchMock = stubApi({ ...tenants, 'POST /api/platform/v1/auth/sign-out': () => ({ body: {} }) })
    render(<App />)

    await userEvent.setup().click(await screen.findByRole('button', { name: 'Sign out' }))

    expect(await screen.findByText('You have signed out.')).toBeInTheDocument()
    expect(fetchMock.mock.calls.some(([url, init]) => url === '/api/platform/v1/auth/sign-out' && init?.method === 'POST')).toBe(true)
  })

  it('does not claim to have signed out when the server did not confirm it', async () => {
    stubApi({ ...tenants, 'POST /api/platform/v1/auth/sign-out': () => ({ status: 500 }) })
    render(<App />)
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Sign out' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('could not be confirmed, so this session may still be active')
    expect(screen.queryByRole('heading', { name: 'Operator sign-in' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Sign out' })).toBeEnabled()
  })

  it('explains a sign-out refused for a missing security token', async () => {
    stubApi({ ...tenants, 'POST /api/platform/v1/auth/sign-out': () => ({ status: 403, body: { problemCode: 'csrf_failed' } }) })
    render(<App />)

    await userEvent.setup().click(await screen.findByRole('button', { name: 'Sign out' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('lost its security token, so this session is still active')
  })

  it('treats a session that had already ended as signed out', async () => {
    stubApi({ ...tenants, 'POST /api/platform/v1/auth/sign-out': () => ({ status: 401, body: { problemCode: 'session_revoked' } }) })
    render(<App />)

    await userEvent.setup().click(await screen.findByRole('button', { name: 'Sign out' }))

    expect(await screen.findByText('You have signed out.')).toBeInTheDocument()
  })

  it('returns to sign-in, saying why, when a page finds the session has ended', async () => {
    stubApi({
      'GET /api/platform/v1/tenants': () => ({ status: 401, body: { problemCode: 'session_revoked' } }),
    })
    render(<App />)

    expect(await screen.findByRole('heading', { name: 'Operator sign-in' })).toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('You were signed out.')
  })
})

it('refuses to be used outside its provider', () => {
  vi.spyOn(console, 'error').mockImplementation(() => {})
  expect(() => renderHook(() => useSession())).toThrow('inside a SessionProvider')
})
