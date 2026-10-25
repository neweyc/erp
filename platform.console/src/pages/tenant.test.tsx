import { render, screen, waitFor, within } from '@testing-library/react'
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
  statusChangedAt: null, apps: ['tickets'], subdomain: 'acme-fire', signInUrl: 'https://acme-fire.app.example.com',
  addresses: [
    { subdomain: 'acme-fire', assignedAt: '2026-10-05T09:00:00Z', replacedAt: null },
    { subdomain: 'acme', assignedAt: '2026-10-01T09:00:00Z', replacedAt: '2026-10-05T09:00:00Z' },
  ],
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

describe('its address', () => {
  it('shows where its people sign in, and the addresses it had before', async () => {
    show(acme)

    const link = await screen.findByRole('link', { name: 'https://acme-fire.app.example.com' })
    expect(link).toHaveAttribute('href', 'https://acme-fire.app.example.com')
    expect(screen.getByRole('list', { name: 'Former addresses' })).toHaveTextContent('acme')
  })

  it('says when a tenant has no address, and lists no former ones when there are none', async () => {
    show({ ...acme, subdomain: null, signInUrl: null, addresses: [] })

    expect(await screen.findByText(/has no address, so nobody can sign in/)).toBeInTheDocument()
    expect(screen.queryByRole('list', { name: 'Former addresses' })).not.toBeInTheDocument()
  })

  it('changes it after saying what happens to the old one, then reads the tenant again', async () => {
    let reads = 0
    const fetchMock = show(() => ({ body: reads++ === 0 ? acme : { ...acme, subdomain: 'acme-rescue' } }), {
      [`POST ${PATH}/address`]: () => ({ body: { subdomain: 'acme-rescue' } }),
    })
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Change address' }))
    const dialog = screen.getByRole('dialog', { name: "Change Acme Fire's address?" })
    expect(dialog).toHaveTextContent('"acme-fire" stops working at once')
    expect(dialog).toHaveTextContent('stays reserved to this tenant')
    const field = within(dialog).getByLabelText('Address')
    expect(field).toHaveValue('acme-fire')
    await user.clear(field)
    await user.type(field, 'acme-rescue')
    expect(dialog).toHaveTextContent('People sign in at https://acme-rescue.app.example.com')
    await user.click(within(dialog).getByRole('button', { name: 'Change address' }))

    await waitFor(() => expect(reads).toBe(2))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(sent(fetchMock, 'POST', `${PATH}/address`)).toEqual([{ subdomain: 'acme-rescue' }])
  })

  it("shows the platform's words for a refused address, and stays open to try another", async () => {
    show(acme, {
      [`POST ${PATH}/address`]: () => ({
        status: 409, body: { problemCode: 'subdomain_taken', message: 'The address "beta" belongs to another tenant.' },
      }),
    })
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Change address' }))
    const dialog = screen.getByRole('dialog')
    await user.clear(within(dialog).getByLabelText('Address'))
    await user.type(within(dialog).getByLabelText('Address'), 'beta')
    await user.click(within(dialog).getByRole('button', { name: 'Change address' }))

    expect(await within(dialog).findByRole('alert')).toHaveTextContent('belongs to another tenant')
    expect(within(dialog).getByRole('button', { name: 'Change address' })).toBeEnabled()
  })

  it('reports any other failure plainly', async () => {
    show(acme, { [`POST ${PATH}/address`]: () => ({ status: 500, body: { problemCode: 'internal_error' } }) })
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Change address' }))
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Change address' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('The address could not be changed. Try again.')
  })

  it('cannot be closed while a change is pending, and can be cancelled otherwise', async () => {
    const pending = held()
    show(acme, { [`POST ${PATH}/address`]: () => pending.promise })
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Change address' }))
    const dialog = screen.getByRole('dialog')
    await user.click(within(dialog).getByRole('button', { name: 'Change address' }))
    await user.click(within(dialog).getByRole('button', { name: 'Close' }))
    expect(screen.getByRole('dialog')).toBeInTheDocument()

    pending.answer({ status: 409, body: { problemCode: 'already_in_that_state', message: 'This tenant already signs in at "acme-fire".' } })
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('already signs in at')
    await user.click(within(dialog).getByRole('button', { name: 'Close' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Change address' }))
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancel' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('cannot be changed for a retired tenant', async () => {
    show({ ...acme, status: 'Retired' })

    await screen.findByRole('heading', { name: 'Acme Fire', level: 1 })
    expect(screen.queryByRole('button', { name: 'Change address' })).not.toBeInTheDocument()
  })
})
