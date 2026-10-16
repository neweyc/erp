import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { SessionProvider } from '../components/session-provider'
import type { Session } from '../lib/session'
import { Roles } from './roles'

const admin: Session = {
  userId: 'usr_admin',
  email: 'admin@acme.test',
  roles: [{ key: 'core:admin', name: 'Administrator' }],
  permissions: ['core.employee.read', 'core.user.read', 'core.user.manage', 'core.role.manage'],
  tenantName: 'Acme',
  licensedApps: ['tickets'],
  tenantSuspended: false,
}

const permissions = [
  { name: 'core.employee.read', app: 'core', description: 'See the employee roster' },
  { name: 'tickets.ticket.read', app: 'tickets', description: 'See tickets' },
  { name: 'tickets.ticket.close', app: 'tickets', description: 'Close tickets' },
]

const roles = [
  {
    key: 'tickets:agent', name: 'Agent', description: 'Works tickets.', builtIn: true,
    permissions: ['tickets.ticket.read', 'tickets.ticket.close'], holders: 2,
  },
  {
    key: 'rol_clerk', name: 'Clerk', description: '', builtIn: false,
    permissions: ['tickets.ticket.read'], holders: 0,
  },
]

type Handler = (init?: RequestInit) => { status?: number; body: unknown }

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

function base(session: Session = admin): Record<string, Handler> {
  return {
    'GET /api/core/v1/auth/session': () => ({ body: session }),
    'GET /api/core/v1/roles': () => ({ body: roles }),
    'GET /api/core/v1/permissions': () => ({ body: permissions }),
  }
}

function renderRoles() {
  return render(
    <SessionProvider>
      <MemoryRouter>
        <Roles />
      </MemoryRouter>
    </SessionProvider>,
  )
}

afterEach(() => vi.unstubAllGlobals())

describe('roles page', () => {
  it('shows what each role allows in words, and how many hold it', async () => {
    stubApi(base())
    renderRoles()

    const agent = await screen.findByRole('article', { name: 'Agent' })
    expect(within(agent).getByText('Close tickets')).toBeInTheDocument()
    expect(within(agent).getByText('Held by 2 people.')).toBeInTheDocument()
  })

  it('creates a role from the permissions ticked', async () => {
    const fetchMock = stubApi({ ...base(), 'POST /api/core/v1/roles': () => ({ body: {} }) })
    const user = userEvent.setup()
    renderRoles()

    await user.click(await screen.findByRole('button', { name: 'New role' }))
    await user.type(screen.getByLabelText('Name'), 'Closer')
    await user.click(screen.getByRole('checkbox', { name: 'Close tickets' }))
    await user.click(screen.getByRole('button', { name: 'Save role' }))

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        '/api/core/v1/roles',
        expect.objectContaining({
          method: 'POST',
          body: JSON.stringify({ name: 'Closer', description: '', permissions: ['tickets.ticket.close'] }),
        }),
      ),
    )
  })

  it('starts a copy of a built-in role with its permissions ticked', async () => {
    stubApi(base())
    const user = userEvent.setup()
    renderRoles()

    await user.click(await screen.findByRole('button', { name: 'Copy Agent' }))

    expect(screen.getByLabelText('Name')).toHaveValue('Copy of Agent')
    expect(screen.getByRole('checkbox', { name: 'Close tickets' })).toBeChecked()
    expect(screen.getByRole('checkbox', { name: 'See the employee roster' })).not.toBeChecked()
  })

  it('offers no edit or delete for a built-in role', async () => {
    stubApi(base())
    renderRoles()

    await screen.findByRole('article', { name: 'Agent' })
    expect(screen.queryByRole('button', { name: 'Edit Agent' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Delete Agent' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Edit Clerk' })).toBeInTheDocument()
  })

  it('deletes only on a second, explicit click, and explains a refusal', async () => {
    const fetchMock = stubApi({
      ...base(),
      'DELETE /api/core/v1/roles/rol_clerk': () => ({ status: 409, body: { problemCode: 'role_in_use' } }),
    })
    const user = userEvent.setup()
    renderRoles()

    await user.click(await screen.findByRole('button', { name: 'Delete Clerk' }))
    expect(fetchMock).not.toHaveBeenCalledWith('/api/core/v1/roles/rol_clerk', expect.anything())

    await user.click(screen.getByRole('button', { name: 'Confirm: delete Clerk' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/still holds that role/i)
  })

  it('reloads the session after editing a role you hold', async () => {
    let sessionCalls = 0
    stubApi({
      ...base(),
      'GET /api/core/v1/auth/session': () => {
        sessionCalls += 1
        return sessionCalls === 1
          ? { body: { ...admin, roles: [...admin.roles, { key: 'rol_clerk', name: 'Clerk' }] } }
          : { status: 401, body: { problemCode: 'role_changed' } }
      },
      'PUT /api/core/v1/roles/rol_clerk': () => ({ body: {} }),
    })
    const user = userEvent.setup()
    renderRoles()

    await user.click(await screen.findByRole('button', { name: 'Edit Clerk' }))
    await user.click(screen.getByRole('checkbox', { name: 'Close tickets' }))
    await user.click(screen.getByRole('button', { name: 'Save role' }))

    await waitFor(() => expect(sessionCalls).toBe(2))
  })

  it('is read-only without core.role.manage', async () => {
    stubApi(base({ ...admin, permissions: ['core.employee.read', 'core.user.read'] }))
    renderRoles()

    await screen.findByRole('article', { name: 'Clerk' })
    expect(screen.queryByRole('button', { name: 'New role' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Copy|Edit|Delete/ })).not.toBeInTheDocument()
  })
})
