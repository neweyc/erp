import { render, screen, within } from '@testing-library/react'
import { Link, MemoryRouter, Route, Routes } from 'react-router-dom'
import { SessionProvider } from '../components/session'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { TenantDetail } from '../lib/api'
import { held, renderAt, sent, stubApi, type Handler } from '../test-support'
import { TenantPage } from './tenant'

afterEach(() => vi.unstubAllGlobals())

const PATH = '/api/platform/v1/tenants/ten_acme'

const acme: TenantDetail = {
  tenantId: 'ten_acme', name: 'Acme Fire', status: 'Active', createdAt: '2026-10-01T09:00:00Z',
  statusChangedAt: null, apps: ['tickets'],
  grants: [
    { app: 'tickets', grantedAt: '2026-10-02T09:00:00Z', revokedAt: null },
    { app: 'ledger', grantedAt: '2026-10-01T09:00:00Z', revokedAt: '2026-10-03T09:00:00Z' },
  ],
  actions: [
    { id: 1, action: 'tenant.created', operatorEmail: 'op@platform.test', tenantId: 'ten_acme', detail: 'Acme Fire', at: '2026-10-01T09:00:00Z' },
  ],
}

function show(tenant: TenantDetail | Handler, more: Record<string, Handler> = {}) {
  const fetchMock = stubApi({
    [`GET ${PATH}`]: typeof tenant === 'function' ? tenant : () => ({ body: tenant }),
    ...more,
  })
  renderAt('/tenants/ten_acme', '/tenants/:tenantId', <TenantPage />)
  return fetchMock
}

describe("a tenant's page", () => {
  it('shows its status, apps, license history and the actions taken on it', async () => {
    show(acme)

    expect(await screen.findByRole('heading', { name: 'Acme Fire', level: 1 })).toBeInTheDocument()
    expect(screen.getByText('ten_acme')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Revoke Tickets' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'License Ledger' })).toBeInTheDocument()
    const history = screen.getByRole('table', { name: 'License history' })
    expect(within(history).getAllByRole('row')).toHaveLength(3)
    expect(within(history).getByRole('row', { name: /Tickets/ })).toHaveTextContent('—')
    expect(screen.getByRole('table', { name: 'Operator actions' })).toHaveTextContent('Created the tenant')
    // The tenant column is left out here: every row is this tenant.
    expect(within(screen.getByRole('table', { name: 'Operator actions' })).queryByRole('columnheader', { name: /Tenant/ }))
      .not.toBeInTheDocument()
  })

  it('says when its status last changed, and when nothing was ever licensed', async () => {
    show({ ...acme, statusChangedAt: '2026-10-05T09:00:00Z', apps: [], grants: [] })

    expect(await screen.findByText(/status changed/)).toBeInTheDocument()
    expect(screen.getByText('No app has been licensed.')).toBeInTheDocument()
  })

  it('is a clear not-found for an unknown tenant', async () => {
    show(() => ({ status: 404, body: { problemCode: 'not_found' } }))

    expect(await screen.findByRole('heading', { name: 'No such tenant', level: 1 })).toBeInTheDocument()
  })

  it('says so when it cannot be read', async () => {
    show(() => ({ status: 500, body: { problemCode: 'internal_error' } }))

    expect(await screen.findByRole('alert')).toHaveTextContent('could not be read (internal_error)')
  })

  it('keeps what it showed, marked as possibly stale, when reading again fails', async () => {
    let calls = 0
    show(() => (calls++ === 0 ? { body: acme } : { status: 500, body: { problemCode: 'internal_error' } }))

    await userEvent.setup().click(await screen.findByRole('button', { name: 'Refresh' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('may be out of date')
    expect(screen.getByRole('heading', { name: 'Acme Fire', level: 1 })).toBeInTheDocument()
  })
})

describe('changing status', () => {
  it('suspends only once the tenant has been named, and says what its people will experience', async () => {
    let status: TenantDetail['status'] = 'Active'
    const fetchMock = show(() => ({ body: { ...acme, status } }), {
      [`POST ${PATH}/status`]: () => {
        status = 'Suspended'
        return { body: {} }
      },
    })
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Suspend' }))
    const dialog = screen.getByRole('dialog', { name: 'Suspend Acme Fire?' })
    expect(dialog).toHaveTextContent('stopped from using the platform at once')
    const confirm = within(dialog).getByRole('button', { name: 'Suspend' })
    expect(confirm).toBeDisabled()

    await user.type(within(dialog).getByLabelText('Reason (optional)'), 'Unpaid invoice')
    await user.type(within(dialog).getByLabelText('Type Acme Fire to confirm'), 'Acme Fire')
    await user.click(confirm)

    expect(await screen.findByRole('button', { name: 'Resume' })).toBeInTheDocument()
    expect(sent(fetchMock, 'POST', `${PATH}/status`)).toEqual([{ status: 'Suspended', reason: 'Unpaid invoice' }])
  })

  it('does not submit on Enter before the name is typed', async () => {
    const fetchMock = show(acme)
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Retire' }))
    await user.type(screen.getByLabelText('Type Acme Fire to confirm'), 'Acme{Enter}')

    expect(sent(fetchMock, 'POST', `${PATH}/status`)).toEqual([])
    expect(screen.getByRole('dialog')).toHaveTextContent('cannot be undone')
  })

  it('resumes without typing the name, since nothing is taken away', async () => {
    const fetchMock = show({ ...acme, status: 'Suspended' }, { [`POST ${PATH}/status`]: () => ({ body: {} }) })
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Resume' }))
    expect(screen.queryByLabelText(/to confirm/)).not.toBeInTheDocument()
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Resume' }))

    expect(sent(fetchMock, 'POST', `${PATH}/status`)).toEqual([{ status: 'Active', reason: '' }])
  })

  it('offers nothing for a retired tenant', async () => {
    show({ ...acme, status: 'Retired' })

    expect(await screen.findByText(/cannot be changed/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Suspend|Resume|Retire|License|Revoke/ })).not.toBeInTheDocument()
  })

  it('reports a change someone else already made, and reads the tenant again', async () => {
    let calls = 0
    show(() => ({ body: { ...acme, name: calls++ === 0 ? 'Acme Fire' : 'Acme Fire Ltd' } }), {
      [`POST ${PATH}/status`]: () => ({ status: 409, body: { problemCode: 'already_in_that_state' } }),
    })
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Suspend' }))
    await user.type(screen.getByLabelText('Type Acme Fire to confirm'), 'Acme Fire')
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Suspend' }))

    expect(await screen.findByText(/Someone else already made that change/)).toBeInTheDocument()
    expect(await screen.findByRole('heading', { name: 'Acme Fire Ltd', level: 1 })).toBeInTheDocument()
  })

  it('can be cancelled, by its own button or its close button', async () => {
    show(acme)
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Suspend' }))
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancel' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Suspend' }))
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Close' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })
})

describe('licensing', () => {
  it('licenses an app', async () => {
    const fetchMock = show(acme, { [`POST ${PATH}/entitlements`]: () => ({ body: {} }) })

    await userEvent.setup().click(await screen.findByRole('button', { name: 'License Ledger' }))

    expect(sent(fetchMock, 'POST', `${PATH}/entitlements`)).toEqual([{ app: 'ledger', licensed: true }])
  })

  it('revokes an app after saying that nothing is deleted', async () => {
    const fetchMock = show(acme, { [`POST ${PATH}/entitlements`]: () => ({ body: {} }) })
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Revoke Tickets' }))
    const dialog = screen.getByRole('alertdialog', { name: 'Revoke Tickets for Acme Fire?' })
    expect(dialog).toHaveTextContent('Nothing is deleted')
    await user.click(within(dialog).getByRole('button', { name: 'Revoke' }))

    expect(sent(fetchMock, 'POST', `${PATH}/entitlements`)).toEqual([{ app: 'tickets', licensed: false }])
  })

  it('can back out of revoking', async () => {
    const fetchMock = show(acme)
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Revoke Tickets' }))
    await user.click(screen.getByRole('button', { name: 'Cancel' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(sent(fetchMock, 'POST', `${PATH}/entitlements`)).toEqual([])
  })

  it('names a refusal it has no words for by its code', async () => {
    show(acme, { [`POST ${PATH}/entitlements`]: () => ({ status: 500, body: { problemCode: 'internal_error' } }) })

    await userEvent.setup().click(await screen.findByRole('button', { name: 'License Ledger' }))

    expect(await screen.findByText(/That did not work \(internal_error\)/)).toBeInTheDocument()
  })

  it('treats a failure with no problem code as unknown', async () => {
    stubApi({ [`GET ${PATH}`]: () => ({ body: acme }) })
    renderAt('/tenants/ten_acme', '/tenants/:tenantId', <TenantPage />)
    await screen.findByRole('button', { name: 'License Ledger' })
    vi.stubGlobal('fetch', vi.fn((url: string) =>
      url.endsWith('/entitlements') ? Promise.reject(new TypeError('offline')) : Promise.resolve({ ok: true, status: 200, json: async () => acme } as Response)))

    await userEvent.setup().click(screen.getByRole('button', { name: 'License Ledger' }))

    expect(await screen.findByText(/That did not work \(unknown\)/)).toBeInTheDocument()
  })
})

it("never shows one tenant's data on another's page when a change finishes after moving on", async () => {
  const pending = held()
  const beta: TenantDetail = { ...acme, tenantId: 'ten_beta', name: 'Beta Rescue', actions: [] }
  stubApi({
    [`GET ${PATH}`]: () => ({ body: acme }),
    [`POST ${PATH}/entitlements`]: () => pending.promise,
    'GET /api/platform/v1/tenants/ten_beta': () => ({ body: beta }),
  })
  render(
    <SessionProvider>
      <MemoryRouter initialEntries={['/tenants/ten_acme']}>
        <Link to="/tenants/ten_beta">Open Beta</Link>
        <Routes>
          <Route path="/tenants/:tenantId" element={<TenantPage />} />
        </Routes>
      </MemoryRouter>
    </SessionProvider>,
  )
  const user = userEvent.setup()

  await user.click(await screen.findByRole('button', { name: 'License Ledger' }))
  await user.click(screen.getByRole('link', { name: 'Open Beta' }))
  expect(await screen.findByRole('heading', { name: 'Beta Rescue' })).toBeInTheDocument()

  pending.answer({ body: {} })
  await pending.promise
  await new Promise((resolve) => setTimeout(resolve, 0))
  expect(screen.getByRole('heading', { name: 'Beta Rescue' })).toBeInTheDocument()
  expect(screen.queryByRole('heading', { name: 'Acme Fire' })).not.toBeInTheDocument()
})
