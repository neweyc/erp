import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { SessionProvider } from '../components/session-provider'
import type { Session } from '../lib/session'
import { Users } from './users'

const admin: Session = {
  userId: 'usr_admin',
  email: 'admin@acme.test',
  roles: [{ key: 'core:admin', name: 'Administrator' }],
  permissions: ['core.employee.read', 'core.employee.create', 'core.user.read', 'core.user.manage', 'core.role.manage'],
  tenantName: 'Acme',
  licensedApps: ['tickets'],
  tenantSuspended: false,
}

const grace = { userId: 'usr_grace', email: 'grace@acme.test', status: 'Active', roles: ['core:member'] }

function role(key: string, name: string, builtIn = true) {
  return { key, name, description: `${name} things.`, builtIn, permissions: [], holders: 0 }
}

const roles = [
  role('core:admin', 'Administrator'),
  role('core:member', 'Member'),
  role('tickets:requester', 'Requester'),
  role('tickets:agent', 'Agent'),
  role('rol_clerk', 'Clerk', false),
]

type Handler = (init?: RequestInit) => { status?: number; body: unknown }

/** Routes each request by method and path; records every call so a test can assert what was sent. */
function stubApi(handlers: Record<string, Handler>) {
  const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const key = `${(init?.method ?? 'GET').toUpperCase()} ${String(input)}`
    const handler = handlers[key]
    const { status = 200, body } = handler ? handler(init) : { status: 404, body: { problemCode: 'not_found' } }

    return Promise.resolve({ ok: status < 400, status, json: async () => body } as Response)
  })

  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

function renderUsers() {
  return render(
    <SessionProvider>
      <MemoryRouter>
        <Users />
      </MemoryRouter>
    </SessionProvider>,
  )
}

afterEach(() => vi.unstubAllGlobals())

describe('users page', () => {
  it('names each user’s roles, built-in and custom', async () => {
    stubApi({
      'GET /api/core/v1/auth/session': () => ({ body: admin }),
      'GET /api/core/v1/users': () => ({ body: [{ ...grace, roles: ['core:member', 'rol_clerk'] }] }),
      'GET /api/core/v1/roles': () => ({ body: roles }),
    })
    renderUsers()

    expect(await screen.findByRole('cell', { name: 'Member, Clerk' })).toBeInTheDocument()
  })

  it('sends only what changed, as additions and removals', async () => {
    const fetchMock = stubApi({
      'GET /api/core/v1/auth/session': () => ({ body: admin }),
      'GET /api/core/v1/users': () => ({ body: [{ ...grace, roles: ['core:member', 'tickets:requester'] }] }),
      'GET /api/core/v1/roles': () => ({ body: roles }),
      'PATCH /api/core/v1/users/usr_grace/roles': () => ({ body: {} }),
    })
    const user = userEvent.setup()
    renderUsers()

    await user.click(await screen.findByRole('button', { name: 'Edit roles for grace@acme.test' }))
    await user.click(screen.getByRole('checkbox', { name: /Agent/ }))
    await user.click(screen.getByRole('checkbox', { name: /Requester/ }))
    await user.click(screen.getByRole('checkbox', { name: /Clerk/ }))
    await user.click(screen.getByRole('button', { name: 'Save roles' }))

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        '/api/core/v1/users/usr_grace/roles',
        expect.objectContaining({
          method: 'PATCH',
          body: JSON.stringify({ add: ['tickets:agent', 'rol_clerk'], remove: ['tickets:requester'] }),
        }),
      ),
    )
  })

  it('never removes a role the page does not list', async () => {
    // ledger:controller is held, but the ledger is unlicensed, so /roles does not return it.
    const fetchMock = stubApi({
      'GET /api/core/v1/auth/session': () => ({ body: admin }),
      'GET /api/core/v1/users': () => ({ body: [{ ...grace, roles: ['core:member', 'ledger:controller'] }] }),
      'GET /api/core/v1/roles': () => ({ body: roles }),
      'PATCH /api/core/v1/users/usr_grace/roles': () => ({ body: {} }),
    })
    const user = userEvent.setup()
    renderUsers()

    await user.click(await screen.findByRole('button', { name: 'Edit roles for grace@acme.test' }))
    await user.click(screen.getByRole('checkbox', { name: /Agent/ }))
    await user.click(screen.getByRole('button', { name: 'Save roles' }))

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        '/api/core/v1/users/usr_grace/roles',
        expect.objectContaining({ body: JSON.stringify({ add: ['tickets:agent'], remove: [] }) }),
      ),
    )
  })

  it('explains a refused change in words and shows the real roles again', async () => {
    let saved = false
    stubApi({
      'GET /api/core/v1/auth/session': () => ({ body: admin }),
      'GET /api/core/v1/users': () => ({ body: [{ ...grace, roles: ['core:admin'] }] }),
      'GET /api/core/v1/roles': () => ({ body: roles }),
      'PATCH /api/core/v1/users/usr_grace/roles': () => {
        saved = true
        return { status: 409, body: { problemCode: 'last_admin' } }
      },
    })
    const user = userEvent.setup()
    renderUsers()

    await user.click(await screen.findByRole('button', { name: 'Edit roles for grace@acme.test' }))
    await user.click(screen.getByRole('checkbox', { name: /Administrator/ }))
    await user.click(screen.getByRole('button', { name: 'Save roles' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/nobody else active could manage users/i)
    expect(saved).toBe(true)
    expect(screen.getByRole('cell', { name: 'Administrator' })).toBeInTheDocument()
  })

  it('is read-only without core.user.manage', async () => {
    stubApi({
      'GET /api/core/v1/auth/session': () => ({
        body: { ...admin, permissions: ['core.employee.read', 'core.user.read'] },
      }),
      'GET /api/core/v1/users': () => ({ body: [{ ...grace, roles: ['tickets:agent'] }] }),
      'GET /api/core/v1/roles': () => ({ body: roles }),
    })
    renderUsers()

    expect(await screen.findByRole('cell', { name: 'Agent' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Edit roles/ })).not.toBeInTheDocument()
  })

  it('reloads the session after changing your own roles, so the shell matches them', async () => {
    let sessionCalls = 0
    stubApi({
      'GET /api/core/v1/auth/session': () => {
        sessionCalls += 1
        // The second call is the reload: the server ends the session because the roles changed.
        return sessionCalls === 1 ? { body: admin } : { status: 401, body: { problemCode: 'role_changed' } }
      },
      'GET /api/core/v1/users': () => ({ body: [{ ...grace, userId: 'usr_admin', email: 'admin@acme.test', roles: ['core:admin'] }] }),
      'GET /api/core/v1/roles': () => ({ body: roles }),
      'PATCH /api/core/v1/users/usr_admin/roles': () => ({ body: {} }),
    })
    const user = userEvent.setup()
    renderUsers()

    await user.click(await screen.findByRole('button', { name: 'Edit roles for admin@acme.test' }))
    await user.click(screen.getByRole('checkbox', { name: /Agent/ }))
    await user.click(screen.getByRole('button', { name: 'Save roles' }))

    await waitFor(() => expect(sessionCalls).toBe(2))
  })
})
