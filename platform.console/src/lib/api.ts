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
  /** The current address (acme-fire), and the full one its people sign in at. */
  readonly subdomain: string | null
  readonly signInUrl: string | null
}

/** An address the tenant has had. The current one has no `replacedAt`. */
export interface Address {
  readonly subdomain: string
  readonly assignedAt: string
  readonly replacedAt: string | null
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
  readonly addresses: readonly Address[]
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
  address: (id: string) => `/api/platform/v1/tenants/${encodeURIComponent(id)}/address`,
  errors: '/api/platform/v1/errors',
  audit: '/api/platform/v1/audit',
}

export function appName(id: string): string {
  return LICENSABLE_APPS.find((a) => a.id === id)?.name ?? id
}

/**
 * A first suggestion for a tenant's address, from its name: "Acme Fire & Rescue" -> acme-fire-rescue.
 * Only a suggestion: the operator can change it, and platform.api decides whether it can be used.
 */
export function suggestSubdomain(name: string): string {
  return name
    .normalize('NFKD')
    .replace(/[\u0300-\u036f]/g, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 40)
    .replace(/-+$/, '')
}

/**
 * The shape of an address, as TenantAddress (packages/tenancy) checks it: 3 to 40 lowercase letters,
 * digits and single hyphens, a letter first and a letter or digit last. The reserved names are left
 * to platform.api, which decides; this only keeps the console from showing an address that is not one.
 */
const ADDRESS_SHAPE = /^(?=.{3,40}$)[a-z](?:[a-z0-9]|-(?!-))*[a-z0-9]$/

/**
 * The address an operator is choosing, in full: acme-fire under https://app.example.com. Null until
 * it has an address's shape: the URL API ignores or cuts short a host it cannot use ("acme fire"),
 * which would show an address other than the one being submitted.
 */
export function signInUrlFor(subdomain: string, tenantBaseUrl: string): string | null {
  if (!ADDRESS_SHAPE.test(subdomain)) return null
  const url = new URL(tenantBaseUrl)
  url.hostname = `${subdomain}.${url.hostname}`
  return url.origin
}

/** A moment as an operator reads it: date and time, in their own time zone. */
export function formatMoment(iso: string): string {
  return new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })
}
