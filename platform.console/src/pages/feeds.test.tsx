import { screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { AuditEntry, ErrorOccurrence } from '../lib/api'
import { renderAt, stubApi } from '../test-support'
import { AuditLog, describeAction } from './audit-log'
import { ErrorFeed } from './error-feed'

afterEach(() => vi.unstubAllGlobals())

describe('the audit log', () => {
  const entries: AuditEntry[] = [
    { id: 2, action: 'tenant.suspended', operatorEmail: 'op@platform.test', tenantId: 'ten_acme', detail: 'Active -> Suspended. Unpaid', at: '2026-10-02T09:00:00Z' },
    { id: 1, action: 'operator.sign_in_failed', operatorEmail: null, tenantId: null, detail: 'x@y.test: bad password', at: '2026-10-01T09:00:00Z' },
  ]

  it('lists every operator action in words, linking the tenant it touched', async () => {
    stubApi({ 'GET /api/platform/v1/audit': () => ({ body: entries }) })
    renderAt('/audit', '/audit', <AuditLog />)

    const table = await screen.findByRole('table', { name: 'Operator actions' })
    const suspended = within(table).getByRole('row', { name: /Suspended the tenant/ })
    expect(within(suspended).getByRole('link', { name: 'ten_acme' })).toHaveAttribute('href', '/tenants/ten_acme')
    // A failed sign-in has nobody signed in to name.
    expect(within(table).getByRole('row', { name: /Failed to sign in/ })).toHaveTextContent('Nobody signed in')
  })

  it('shows an action it has no words for as recorded', () => {
    expect(describeAction('tenant.renamed')).toBe('tenant.renamed')
  })

  it('says when there is nothing yet, and when it cannot be read', async () => {
    stubApi({ 'GET /api/platform/v1/audit': () => ({ body: [] }) })
    const { unmount } = renderAt('/audit', '/audit', <AuditLog />)
    expect(await screen.findByText('No operator actions recorded')).toBeInTheDocument()
    unmount()

    stubApi({ 'GET /api/platform/v1/audit': () => ({ status: 500, body: { problemCode: 'internal_error' } }) })
    renderAt('/audit', '/audit', <AuditLog />)
    expect(await screen.findByRole('alert')).toHaveTextContent('could not be read (internal_error)')
  })
})

describe('the error feed', () => {
  const errors: ErrorOccurrence[] = [
    { reference: 'ERR-ABC123', fingerprint: 'f00dfeed', app: 'core', tenantId: 'ten_acme', count: 3, occurredAt: '2026-10-02T09:00:00Z' },
    { reference: 'ERR-DEF456', fingerprint: 'deadbeef', app: 'platform', tenantId: null, count: 1, occurredAt: '2026-10-01T09:00:00Z' },
  ]

  it('lists occurrences by reference, linking the tenant when there is one', async () => {
    stubApi({ 'GET /api/platform/v1/errors': () => ({ body: errors }) })
    renderAt('/errors', '/errors', <ErrorFeed />)

    const table = await screen.findByRole('table', { name: 'Errors' })
    const first = within(table).getByRole('row', { name: /ERR-ABC123/ })
    expect(within(first).getByRole('link', { name: 'ten_acme' })).toBeInTheDocument()
    expect(within(table).getByRole('row', { name: /ERR-DEF456/ })).toHaveTextContent('None')
  })

  it('says when there are none, and when it cannot be read', async () => {
    stubApi({ 'GET /api/platform/v1/errors': () => ({ body: [] }) })
    const { unmount } = renderAt('/errors', '/errors', <ErrorFeed />)
    expect(await screen.findByText('No errors in the last 30 days')).toBeInTheDocument()
    unmount()

    stubApi({ 'GET /api/platform/v1/errors': () => ({ status: 500, body: { problemCode: 'internal_error' } }) })
    renderAt('/errors', '/errors', <ErrorFeed />)
    expect(await screen.findByRole('alert')).toHaveTextContent('could not be read (internal_error)')
  })
})
