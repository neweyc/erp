/**
 * What the console reads from platform.api, and the one place its routes are spelled. The console
 * talks to platform.api only; nothing here reaches a customer API.
 */
export { api, ApiError } from '@app-platform/web-api'

export type TenantStatus = 'Active' | 'Suspended' | 'Retired'

/** An app a tenant can license. Core is absent: it is always on and cannot be licensed or revoked. */
export const LICENSABLE_APPS = [
  { id: 'tickets', name: 'Tickets' },
  { id: 'ledger', name: 'Ledger' },
] as const

export interface TenantSummary {
  readonly tenantId: string
  readonly name: string
  readonly status: TenantStatus
  readonly createdAt: string
  readonly statusChangedAt: string | null
  readonly apps: readonly string[]
}

export interface Grant {
  readonly app: string
  readonly grantedAt: string
  readonly revokedAt: string | null
}

export interface AuditEntry {
  readonly id: number
  readonly action: string
  /** Null for a failed sign-in, which has no operator yet. */
  readonly operatorEmail: string | null
  readonly tenantId: string | null
  readonly detail: string | null
  readonly at: string
}

export interface TenantDetail extends TenantSummary {
  readonly grants: readonly Grant[]
  readonly actions: readonly AuditEntry[]
}

export interface ErrorOccurrence {
  readonly reference: string
  readonly fingerprint: string
  readonly app: string
  readonly tenantId: string | null
  readonly count: number
  readonly occurredAt: string
}

export const routes = {
  session: '/api/platform/v1/auth/session',
  signIn: '/api/platform/v1/auth/sign-in',
  signOut: '/api/platform/v1/auth/sign-out',
  tenants: '/api/platform/v1/tenants',
  tenant: (id: string) => `/api/platform/v1/tenants/${encodeURIComponent(id)}`,
  status: (id: string) => `/api/platform/v1/tenants/${encodeURIComponent(id)}/status`,
  entitlements: (id: string) => `/api/platform/v1/tenants/${encodeURIComponent(id)}/entitlements`,
  errors: '/api/platform/v1/errors',
  audit: '/api/platform/v1/audit',
}

export function appName(id: string): string {
  return LICENSABLE_APPS.find((a) => a.id === id)?.name ?? id
}

/** A moment as an operator reads it: date and time, in their own time zone. */
export function formatMoment(iso: string): string {
  return new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })
}
