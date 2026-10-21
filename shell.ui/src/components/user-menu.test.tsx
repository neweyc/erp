import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Session } from '../lib/session'
import { SessionProvider, useSession } from './session-provider'
import { UserMenu } from './user-menu'

const session: Session = {
  userId: 'usr_1',
  email: 'ada@acme.test',
  roles: [{ key: 'core:admin', name: 'Administrator' }],
  permissions: ['core.user.read'],
  tenantName: 'Acme',
  licensedApps: [],
  tenantSuspended: false,
}

/** Shows what the sign-in page would be told, once the menu has signed out. */
function Probe() {
  const { session: current, problemCode } = useSession()
  return <p>{current ? 'signed in' : `signed out: ${problemCode}`}</p>
}

function renderMenu() {
  const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const key = `${(init?.method ?? 'GET').toUpperCase()} ${String(input)}`
    if (key === 'GET /api/core/v1/auth/session') return Promise.resolve({ ok: true, status: 200, json: async () => session } as Response)
    if (key === 'POST /api/core/v1/auth/sign-out') return Promise.resolve({ ok: true, status: 204, json: async () => ({}) } as Response)
    return Promise.resolve({ ok: false, status: 404, json: async () => ({ problemCode: 'not_found' }) } as Response)
  })
  vi.stubGlobal('fetch', fetchMock)

  render(
    <SessionProvider>
      <MemoryRouter>
        <UserMenu />
        <Probe />
      </MemoryRouter>
    </SessionProvider>,
  )
  return fetchMock
}

afterEach(() => {
  vi.unstubAllGlobals()
  localStorage.clear()
  document.documentElement.classList.remove('dark')
})

describe('account menu', () => {
  it('signs out on the server, then says so on the sign-in page', async () => {
    const user = userEvent.setup()
    const fetchMock = renderMenu()

    await user.click(await screen.findByRole('button', { name: 'Account: ada@acme.test' }))
    await user.click(screen.getByRole('menuitem', { name: 'Sign out' }))

    await waitFor(() => expect(screen.getByText('signed out: signed_out')).toBeInTheDocument())
    expect(fetchMock).toHaveBeenCalledWith('/api/core/v1/auth/sign-out', expect.objectContaining({ method: 'POST' }))
  })

  it('applies and remembers a theme chosen from it', async () => {
    const user = userEvent.setup()
    renderMenu()

    await user.click(await screen.findByRole('button', { name: 'Account: ada@acme.test' }))
    await user.click(screen.getByRole('menuitemradio', { name: 'Dark' }))

    expect(document.documentElement).toHaveClass('dark')
    expect(localStorage.getItem('theme')).toBe('dark')
  })
})
