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
  role: 'admin',
  appRoles: {},
  permissions: ['core.employee.read', 'core.employee.create', 'core.user.read', 'core.user.manage'],
  tenantName: 'Acme',
  licensedApps: ['tickets'],
  tenantSuspended: false,
}

const grace = { userId: 'usr_grace', email: 'grace@acme.test', status: 'Active', role: 'member', appRoles: {} }

const roles = [
  { app: 'core', name: 'admin', description: 'Manages people.', permissions: [] },
  { app: 'core', name: 'member', description: 'Sees the roster.', permissions: [] },
  { app: 'tickets', name: 'requester', description: 'Raises tickets.', permissions: [] },
  { app: 'tickets', name: 'agent', description: 'Works tickets.', permissions: [] },
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
  it('saves an app role the moment it is chosen, sending only that app', async () => {
    const fetchMock = stubApi({
      'GET /api/core/v1/auth/session': () => ({ body: admin }),
      'GET /api/core/v1/users': () => ({ body: [grace] }),
      'GET /api/core/v1/roles': () => ({ body: roles }),
      'PUT /api/core/v1/users/usr_grace/roles': () => ({ body: {} }),
    })
    const user = userEvent.setup()
    renderUsers()

    await user.selectOptions(await screen.findByLabelText('Tickets role for grace@acme.test'), 'agent')

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        '/api/core/v1/users/usr_grace/roles',
        expect.objectContaining({ method: 'PUT', body: JSON.stringify({ appRoles: { tickets: 'agent' } }) }),
      ),
    )
  })

  it('sends null to take an app role away', async () => {
    const fetchMock = stubApi({
      'GET /api/core/v1/auth/session': () => ({ body: admin }),
      'GET /api/core/v1/users': () => ({ body: [{ ...grace, appRoles: { tickets: 'agent' } }] }),
      'GET /api/core/v1/roles': () => ({ body: roles }),
      'PUT /api/core/v1/users/usr_grace/roles': () => ({ body: {} }),
    })
    const user = userEvent.setup()
    renderUsers()

    await user.selectOptions(await screen.findByLabelText('Tickets role for grace@acme.test'), 'No access')

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        '/api/core/v1/users/usr_grace/roles',
        expect.objectContaining({ body: JSON.stringify({ appRoles: { tickets: null } }) }),
      ),
    )
  })

  it('explains a refused change in words and shows the real value again', async () => {
    stubApi({
      'GET /api/core/v1/auth/session': () => ({ body: admin }),
      'GET /api/core/v1/users': () => ({ body: [{ ...grace, role: 'admin' }] }),
      'GET /api/core/v1/roles': () => ({ body: roles }),
      'PUT /api/core/v1/users/usr_grace/roles': () => ({ status: 409, body: { problemCode: 'last_admin' } }),
    })
    const user = userEvent.setup()
    renderUsers()

    const select = await screen.findByLabelText('Role for grace@acme.test')
    await user.selectOptions(select, 'member')

    expect(await screen.findByRole('alert')).toHaveTextContent(/only active administrator/i)
    await waitFor(() => expect(screen.getByLabelText('Role for grace@acme.test')).toHaveValue('admin'))
  })

  it('is read-only without core.user.manage', async () => {
    stubApi({
      'GET /api/core/v1/auth/session': () => ({
        body: { ...admin, permissions: ['core.employee.read', 'core.user.read'] },
      }),
      'GET /api/core/v1/users': () => ({ body: [{ ...grace, appRoles: { tickets: 'agent' } }] }),
      'GET /api/core/v1/roles': () => ({ body: roles }),
    })
    renderUsers()

    expect(await screen.findByText('grace@acme.test')).toBeInTheDocument()
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument()
    expect(screen.getByRole('cell', { name: 'agent' })).toBeInTheDocument()
  })

  it('reloads the session after changing your own roles, so the shell matches them', async () => {
    let sessionCalls = 0
    stubApi({
      'GET /api/core/v1/auth/session': () => {
        sessionCalls += 1
        // The second call is the reload: the server ends the session because the roles changed.
        return sessionCalls === 1
          ? { body: admin }
          : { status: 401, body: { problemCode: 'role_changed' } }
      },
      'GET /api/core/v1/users': () => ({ body: [{ ...grace, userId: 'usr_admin', email: 'admin@acme.test' }] }),
      'GET /api/core/v1/roles': () => ({ body: roles }),
      'PUT /api/core/v1/users/usr_admin/roles': () => ({ body: {} }),
    })
    const user = userEvent.setup()
    renderUsers()

    await user.selectOptions(await screen.findByLabelText('Tickets role for admin@acme.test'), 'agent')

    await waitFor(() => expect(sessionCalls).toBe(2))
  })
})
