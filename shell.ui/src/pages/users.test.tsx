import { render, screen, waitFor, within } from '@testing-library/react'
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

const grace = { userId: 'usr_grace', email: 'grace@acme.test', status: 'Active', roles: ['core:member'], invitation: null }

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
  // The roster feeds "Invite someone"; empty unless a test says otherwise.
  const routes: Record<string, Handler> = { 'GET /api/core/v1/employees': () => ({ body: [] }), ...handlers }
  const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const key = `${(init?.method ?? 'GET').toUpperCase()} ${String(input)}`
    const handler = routes[key]
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

    // Said inside the dialog, which stays open so the choices made are not lost.
    expect(await screen.findByRole('alert')).toHaveTextContent(/nobody else active could manage users/i)
    expect(saved).toBe(true)

    // Behind it, the list shows what the server holds, not what was picked.
    await user.click(screen.getByRole('button', { name: 'Cancel' }))
    expect(await screen.findByRole('cell', { name: 'Administrator' })).toBeInTheDocument()
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

  it('deactivates only on a second, explicit click', async () => {
    const fetchMock = stubApi({
      'GET /api/core/v1/auth/session': () => ({ body: admin }),
      'GET /api/core/v1/users': () => ({ body: [grace] }),
      'GET /api/core/v1/roles': () => ({ body: roles }),
      'POST /api/core/v1/users/usr_grace/deactivate': () => ({ body: {} }),
    })
    const user = userEvent.setup()
    renderUsers()

    await user.click(await screen.findByRole('button', { name: 'Deactivate grace@acme.test' }))
    expect(fetchMock).not.toHaveBeenCalledWith('/api/core/v1/users/usr_grace/deactivate', expect.anything())

    await user.click(screen.getByRole('button', { name: 'Confirm: deactivate grace@acme.test' }))

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        '/api/core/v1/users/usr_grace/deactivate',
        expect.objectContaining({ method: 'POST' }),
      ),
    )
    expect(await screen.findByText(/deactivated and signed out/)).toBeInTheDocument()
  })

  it('offers no way to deactivate yourself', async () => {
    stubApi({
      'GET /api/core/v1/auth/session': () => ({ body: admin }),
      'GET /api/core/v1/users': () => ({ body: [{ ...grace, userId: 'usr_admin', email: 'admin@acme.test' }] }),
      'GET /api/core/v1/roles': () => ({ body: roles }),
    })
    renderUsers()

    await screen.findByRole('button', { name: 'Edit roles for admin@acme.test' })
    expect(screen.queryByRole('button', { name: /Deactivate/ })).not.toBeInTheDocument()
  })

  it('explains why the last administrator cannot be deactivated', async () => {
    stubApi({
      'GET /api/core/v1/auth/session': () => ({ body: admin }),
      'GET /api/core/v1/users': () => ({ body: [{ ...grace, roles: ['core:admin'] }] }),
      'GET /api/core/v1/roles': () => ({ body: roles }),
      'POST /api/core/v1/users/usr_grace/deactivate': () => ({ status: 409, body: { problemCode: 'last_admin' } }),
    })
    const user = userEvent.setup()
    renderUsers()

    await user.click(await screen.findByRole('button', { name: 'Deactivate grace@acme.test' }))
    await user.click(screen.getByRole('button', { name: 'Confirm: deactivate grace@acme.test' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/nobody else active could manage users/i)
  })

  it('reactivates a deactivated account', async () => {
    const fetchMock = stubApi({
      'GET /api/core/v1/auth/session': () => ({ body: admin }),
      'GET /api/core/v1/users': () => ({ body: [{ ...grace, status: 'Deactivated' }] }),
      'GET /api/core/v1/roles': () => ({ body: roles }),
      'POST /api/core/v1/users/usr_grace/reactivate': () => ({ body: {} }),
    })
    const user = userEvent.setup()
    renderUsers()

    expect(await screen.findByRole('cell', { name: 'Deactivated' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /^Deactivate/ })).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Reactivate grace@acme.test' }))

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        '/api/core/v1/users/usr_grace/reactivate',
        expect.objectContaining({ method: 'POST' }),
      ),
    )
  })

  it('says when an invitation email failed, and resends it', async () => {
    const invited = {
      ...grace,
      status: 'Invited',
      invitation: { delivery: 'failed', expiresAt: '2026-10-24T09:00:00Z', expired: false },
    }
    const fetchMock = stubApi({
      'GET /api/core/v1/auth/session': () => ({ body: admin }),
      'GET /api/core/v1/users': () => ({ body: [invited] }),
      'GET /api/core/v1/roles': () => ({ body: roles }),
      'POST /api/core/v1/users/usr_grace/invitation': () => ({ body: {} }),
    })
    const user = userEvent.setup()
    renderUsers()

    expect(await screen.findByRole('cell', { name: 'Invited: email could not be delivered' })).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Resend invitation to grace@acme.test' }))

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        '/api/core/v1/users/usr_grace/invitation',
        expect.objectContaining({ method: 'POST' }),
      ),
    )
    expect(await screen.findByText(/new invitation is on its way/)).toBeInTheDocument()
  })

  it('says when an invitation link has expired', async () => {
    stubApi({
      'GET /api/core/v1/auth/session': () => ({ body: admin }),
      'GET /api/core/v1/users': () => ({
        body: [{ ...grace, status: 'Invited', invitation: { delivery: 'delivered', expiresAt: '2026-10-01T09:00:00Z', expired: true } }],
      }),
      'GET /api/core/v1/roles': () => ({ body: roles }),
    })
    renderUsers()

    expect(await screen.findByRole('cell', { name: 'Invited: link expired' })).toBeInTheDocument()
  })

  it('invites someone from the roster, offering only people without an account', async () => {
    const roster = [
      { employeeId: 'emp_grace', firstName: 'Grace', lastName: 'Hopper', displayName: 'Grace Hopper', email: 'grace@acme.test', status: 'Active' },
      { employeeId: 'emp_ada', firstName: 'Ada', lastName: 'Lovelace', displayName: 'Ada Lovelace', email: 'ada@acme.test', status: 'Active' },
      { employeeId: 'emp_linus', firstName: 'Linus', lastName: 'Torvalds', displayName: 'Linus Torvalds', email: null, status: 'Active' },
    ]
    const fetchMock = stubApi({
      'GET /api/core/v1/auth/session': () => ({ body: admin }),
      'GET /api/core/v1/users': () => ({ body: [{ ...grace, employeeId: 'emp_grace' }] }),
      'GET /api/core/v1/roles': () => ({ body: roles }),
      'GET /api/core/v1/employees': () => ({ body: roster }),
      'POST /api/core/v1/employees/emp_ada/invite': () => ({ body: {} }),
    })
    const user = userEvent.setup()
    renderUsers()

    await screen.findByRole('cell', { name: 'Member' })
    await user.click(screen.getByRole('button', { name: 'Invite someone' }))

    // Grace already has an account and Linus has no address: only Ada can be invited.
    expect(screen.getAllByRole('option').map((o) => o.textContent)).toEqual(['Ada Lovelace (ada@acme.test)'])
    await user.click(screen.getByRole('button', { name: 'Send invitation' }))

    expect(await screen.findByText('An invitation is on its way to ada@acme.test.')).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/core/v1/employees/emp_ada/invite',
      expect.objectContaining({ method: 'POST', body: JSON.stringify({ roles: ['core:member'], email: 'ada@acme.test' }) }),
    )
  })

  it('puts an account that is not on the roster onto it', async () => {
    const fetchMock = stubApi({
      'GET /api/core/v1/auth/session': () => ({ body: admin }),
      'GET /api/core/v1/users': () => ({ body: [{ ...grace, employeeId: null }] }),
      'GET /api/core/v1/roles': () => ({ body: roles }),
      'POST /api/core/v1/users/usr_grace/employee': () => ({ body: {} }),
    })
    const user = userEvent.setup()
    renderUsers()

    expect(await screen.findByText('Not on the roster')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Add grace@acme.test to the roster' }))
    await user.type(screen.getByLabelText('First name'), 'Grace')
    await user.type(screen.getByLabelText('Last name'), 'Hopper')
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Add to roster' }))

    expect(await screen.findByText('grace@acme.test is on the roster.')).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/core/v1/users/usr_grace/employee',
      expect.objectContaining({ body: JSON.stringify({ firstName: 'Grace', lastName: 'Hopper' }) }),
    )
  })
})
