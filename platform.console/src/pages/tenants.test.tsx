import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { TenantSummary } from '../lib/api'
import { held, renderAt, sent, stubApi } from '../test-support'
import { Tenants } from './tenants'

afterEach(() => vi.unstubAllGlobals())

const acme: TenantSummary = {
  tenantId: 'ten_acme', name: 'Acme Fire', status: 'Active', createdAt: '2026-10-01T09:00:00Z',
  statusChangedAt: null, apps: ['tickets', 'ledger'],
}
const beta: TenantSummary = { ...acme, tenantId: 'ten_beta', name: 'Beta Rescue', status: 'Suspended', apps: [] }

const show = () => renderAt('/tenants', '/tenants', <Tenants />)

describe('the tenant list', () => {
  it('shows each tenant with its status and licensed apps, and when it was read', async () => {
    stubApi({ 'GET /api/platform/v1/tenants': () => ({ body: [acme, beta] }) })
    show()

    const table = await screen.findByRole('table', { name: 'Tenants' })
    const acmeRow = within(table).getByRole('row', { name: /Acme Fire/ })
    expect(acmeRow).toHaveTextContent('Active')
    expect(acmeRow).toHaveTextContent('Tickets, Ledger')
    expect(within(table).getByRole('row', { name: /Beta Rescue/ })).toHaveTextContent('SuspendedNone')
    expect(within(table).getByRole('link', { name: 'Acme Fire' })).toHaveAttribute('href', '/tenants/ten_acme')
    expect(screen.getByText(/^Read at /)).toBeInTheDocument()
  })

  it('reads again on Refresh', async () => {
    let calls = 0
    stubApi({ 'GET /api/platform/v1/tenants': () => ({ body: calls++ === 0 ? [] : [acme] }) })
    show()

    expect(await screen.findByText('No tenants yet')).toBeInTheDocument()
    await userEvent.setup().click(screen.getByRole('button', { name: 'Refresh' }))

    expect(await screen.findByRole('link', { name: 'Acme Fire' })).toBeInTheDocument()
  })

  it('says so when the list cannot be read', async () => {
    stubApi({ 'GET /api/platform/v1/tenants': () => ({ status: 500, body: { problemCode: 'internal_error' } }) })
    show()

    expect(await screen.findByRole('alert')).toHaveTextContent('could not be read (internal_error)')
    expect(screen.queryByText('Loading tenants…')).not.toBeInTheDocument()
  })
})

it('shows only the newest read when two finish out of order', async () => {
  const slow = held()
  let reads = 0
  stubApi({
    'GET /api/platform/v1/tenants': () => {
      reads++
      if (reads === 1) return { body: [acme] }
      if (reads === 2) return slow.promise
      return { body: [{ ...acme, status: 'Suspended' }] }
    },
  })
  show()
  const user = userEvent.setup()
  expect(await screen.findByRole('row', { name: /Acme Fire/ })).toHaveTextContent('Active')

  // Refresh starts read 2 and holds it; the dialog's close starts read 3, which finishes first.
  await user.click(screen.getByRole('button', { name: 'Refresh' }))
  await user.click(screen.getByRole('button', { name: 'New tenant' }))
  await user.click(screen.getByRole('button', { name: 'Cancel' }))
  expect(await screen.findByRole('row', { name: /Acme Fire/ })).toHaveTextContent('Suspended')

  slow.answer({ body: [acme] })
  await slow.promise
  await new Promise((resolve) => setTimeout(resolve, 0))
  expect(screen.getByRole('row', { name: /Acme Fire/ })).toHaveTextContent('Suspended')
})

describe('creating a tenant', () => {
  async function fill() {
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'New tenant' }))
    const dialog = screen.getByRole('dialog', { name: 'New tenant' })
    await user.type(within(dialog).getByLabelText('Organization name'), 'Gamma Ltd')
    await user.type(within(dialog).getByLabelText("Administrator's email"), 'admin@gamma.test')
    return { user, dialog }
  }

  it('sends the name, the address and a key, then opens the new tenant', async () => {
    const fetchMock = stubApi({
      'GET /api/platform/v1/tenants': () => ({ body: [] }),
      'POST /api/platform/v1/tenants': () => ({ body: { tenantId: 'ten_gamma' } }),
    })
    show()
    const { user, dialog } = await fill()

    await user.click(within(dialog).getByRole('button', { name: 'Create tenant' }))

    expect(await screen.findByText('Elsewhere')).toBeInTheDocument()
    const [body] = sent(fetchMock, 'POST', '/api/platform/v1/tenants') as { idempotencyKey: string }[]
    expect(body).toMatchObject({ name: 'Gamma Ltd', adminEmail: 'admin@gamma.test' })
    expect(body!.idempotencyKey).toMatch(/^[0-9a-f-]{36}$/)
  })

  it('retries with the same key, so a second press cannot create a second tenant', async () => {
    let attempts = 0
    const fetchMock = stubApi({
      'GET /api/platform/v1/tenants': () => ({ body: [] }),
      'POST /api/platform/v1/tenants': () =>
        attempts++ === 0 ? { status: 400, body: { problemCode: 'provisioning_failed' } } : { body: { tenantId: 'ten_gamma' } },
    })
    show()
    const { user, dialog } = await fill()

    await user.click(within(dialog).getByRole('button', { name: 'Create tenant' }))
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('it will not be created twice')
    await user.click(within(dialog).getByRole('button', { name: 'Create tenant' }))

    expect(await screen.findByText('Elsewhere')).toBeInTheDocument()
    const keys = (sent(fetchMock, 'POST', '/api/platform/v1/tenants') as { idempotencyKey: string }[]).map((b) => b.idempotencyKey)
    expect(keys).toHaveLength(2)
    expect(keys[0]).toBe(keys[1])
  })

  it("shows the platform's own words for a validation failure", async () => {
    stubApi({
      'GET /api/platform/v1/tenants': () => ({ body: [] }),
      'POST /api/platform/v1/tenants': () => ({
        status: 400, body: { problemCode: 'validation_failed', message: 'An admin email is required.' },
      }),
    })
    show()
    const { user, dialog } = await fill()

    await user.click(within(dialog).getByRole('button', { name: 'Create tenant' }))

    expect(await within(dialog).findByRole('alert')).toHaveTextContent('An admin email is required.')
  })

  it('cannot be closed while a create is pending, so a reopened dialog cannot send a second key', async () => {
    const pending = held()
    const fetchMock = stubApi({
      'GET /api/platform/v1/tenants': () => ({ body: [] }),
      'POST /api/platform/v1/tenants': () => pending.promise,
    })
    show()
    const { user, dialog } = await fill()

    await user.click(within(dialog).getByRole('button', { name: 'Create tenant' }))
    expect(within(dialog).getByRole('button', { name: 'Creating…' })).toBeDisabled()
    await user.click(within(dialog).getByRole('button', { name: 'Close' }))
    expect(screen.getByRole('dialog', { name: 'New tenant' })).toBeInTheDocument()

    pending.answer({ status: 400, body: { problemCode: 'provisioning_failed' } })
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('it will not be created twice')
    expect(within(dialog).getByLabelText('Organization name')).toHaveValue('Gamma Ltd')
    expect(sent(fetchMock, 'POST', '/api/platform/v1/tenants')).toHaveLength(1)
  })

  it('reads the list again when closed, since a failed attempt may still have created the tenant', async () => {
    let reads = 0
    stubApi({
      'GET /api/platform/v1/tenants': () => ({ body: reads++ === 0 ? [] : [acme] }),
      'POST /api/platform/v1/tenants': () => ({ status: 504, body: { problemCode: 'provisioning_failed' } }),
    })
    show()
    const { user, dialog } = await fill()

    await user.click(within(dialog).getByRole('button', { name: 'Create tenant' }))
    await within(dialog).findByRole('alert')
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }))

    expect(await screen.findByRole('link', { name: 'Acme Fire' })).toBeInTheDocument()
  })

  it('can be cancelled, by its own button or its close button', async () => {
    stubApi({ 'GET /api/platform/v1/tenants': () => ({ body: [] }) })
    show()
    const { user, dialog } = await fill()

    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'New tenant' }))
    // Each opening starts empty, with a new key.
    expect(screen.getByLabelText('Organization name')).toHaveValue('')
    await user.click(screen.getByRole('button', { name: 'Close' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })
})
