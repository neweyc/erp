import { render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { AppNav } from './app-nav'
import { AppRoutes } from './app-routes'
import { SessionProvider } from './session-provider'
import { resetComponentCache } from '../lib/apps'
import type { Session } from '../lib/session'

const loadTickets = vi.fn()

// Replaced so the test can observe whether the app's module is FETCHED, which is the property
// under test — not merely whether it is rendered.
//
// visibleApps is overridden too, not just APPS. It is defined in the real module and closes
// over the real APPS, so replacing only the constant leaves the filter returning the genuine
// registry — which is exactly the trap that made the first version of this test pass against
// the wrong app.
vi.mock('../lib/apps', async () => {
  const actual = await vi.importActual<typeof import('../lib/apps')>('../lib/apps')

  const apps = [
    {
      id: 'tickets',
      name: 'Tickets',
      path: '/tickets',
      permission: 'tickets.ticket.read',
      load: async () => {
        loadTickets()
        // Renders the permissions it was handed, so a test can see what the shell passed.
        return {
          default: ({ permissions }: { permissions: readonly string[] }) => (
            <>
              <p>Tickets app</p>
              <p>Permissions: {permissions.join(' ')}</p>
            </>
          ),
        }
      },
    },
  ]

  return {
    ...actual,
    APPS: apps,
    visibleApps: (licensed: readonly string[], permissions: readonly string[]) =>
      apps.filter((a) => licensed.includes(a.id) && permissions.includes(a.permission)),
  }
})

function session(overrides: Partial<Session> = {}): Session {
  return {
    userId: 'usr_1',
    email: 'ada@acme.test',
    roles: [
      { key: 'core:member', name: 'Member' },
      { key: 'tickets:requester', name: 'Requester' },
    ],
    permissions: ['core.employee.read', 'tickets.ticket.read', 'tickets.ticket.create'],
    tenantName: 'Acme',
    licensedApps: ['tickets'],
    tenantSuspended: false,
    ...overrides,
  }
}

function renderAt(path: string, current: Session) {
  vi.stubGlobal('fetch', () =>
    Promise.resolve({ ok: true, status: 200, json: async () => current } as Response),
  )

  return render(
    <SessionProvider>
      <MemoryRouter initialEntries={[path]}>
        <AppNav />
        <AppRoutes />
      </MemoryRouter>
    </SessionProvider>,
  )
}

beforeEach(() => {
  resetComponentCache()
  loadTickets.mockClear()
})

afterEach(() => vi.unstubAllGlobals())

describe('entitlement gating', () => {
  it('renders a licensed app and fetches its bundle', async () => {
    renderAt('/tickets', session())

    expect(await screen.findByText('Tickets app')).toBeInTheDocument()
    expect(loadTickets).toHaveBeenCalled()
  })

  it('never fetches the bundle of an unlicensed app, even when routed straight at it', async () => {
    renderAt('/tickets', session({ licensedApps: [] }))

    // The whole point of route-level dynamic import. A route that rendered "not licensed"
    // would already have downloaded the bundle to say so, which both wastes the bytes and
    // tells the reader's browser what features exist that they have not bought.
    expect(await screen.findByText('Page not found')).toBeInTheDocument()
    expect(loadTickets).not.toHaveBeenCalled()
  })

  it('omits an unlicensed app from the nav rather than disabling it', async () => {
    renderAt('/dashboard', session({ licensedApps: [] }))

    await waitFor(() => expect(screen.getByRole('link', { name: 'Dashboard' })).toBeInTheDocument())
    // Absent, not present-and-disabled: a disabled item advertises a capability the reader
    // cannot have and invites a support conversation about it.
    expect(screen.queryByRole('link', { name: 'Tickets' })).not.toBeInTheDocument()
  })

  it('shows a licensed app in the nav', async () => {
    renderAt('/dashboard', session())

    expect(await screen.findByRole('link', { name: 'Tickets' })).toBeInTheDocument()
  })

  it('hides a licensed app from someone with no role in it, and never fetches it', async () => {
    renderAt('/tickets', session({ roles: [{ key: 'core:member', name: 'Member' }], permissions: ['core.employee.read'] }))

    expect(await screen.findByText('Page not found')).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Tickets' })).not.toBeInTheDocument()
    expect(loadTickets).not.toHaveBeenCalled()
  })

  it('passes the caller’s permissions to the app', async () => {
    renderAt('/tickets', session())

    expect(await screen.findByText(/^Permissions:/)).toHaveTextContent(
      'Permissions: core.employee.read tickets.ticket.read tickets.ticket.create',
    )
  })

  it('shows Users and Roles in the nav only with core.user.read', async () => {
    const { unmount } = renderAt('/dashboard', session())
    await screen.findByRole('link', { name: 'Dashboard' })
    expect(screen.queryByRole('link', { name: 'Users' })).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Roles' })).not.toBeInTheDocument()
    unmount()

    renderAt('/dashboard', session({ permissions: ['core.employee.read', 'core.user.read'] }))
    expect(await screen.findByRole('link', { name: 'Users' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Roles' })).toBeInTheDocument()
  })

  it('has no Roles route without core.user.read', async () => {
    renderAt('/roles', session())

    expect(await screen.findByText('Page not found')).toBeInTheDocument()
  })

  it('tells the reader an app may simply not be enabled, rather than leaving a bare 404', async () => {
    renderAt('/tickets', session({ licensedApps: [] }))

    expect(
      await screen.findByText(/not enabled for your organisation/i),
    ).toBeInTheDocument()
  })
})
