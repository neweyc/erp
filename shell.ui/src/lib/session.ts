import { api } from '@app-platform/web-api'
export type Role = 'admin' | 'manager' | 'member'

export interface Session {
  readonly userId: string
  readonly email: string
  readonly role: Role
  readonly tenantName: string
  /** Apps the tenant has licensed. Empty is legitimate — a tenant may license none. */
  readonly licensedApps: readonly string[]
  /** True when the tenant is suspended: the principal is valid, most routes are not. */
  readonly tenantSuspended: boolean
}

export { api, ApiError } from '@app-platform/web-api'
export type { ApiProblem } from '@app-platform/web-api'

export function fetchSession(): Promise<Session> {
  return api<Session>('/api/core/v1/auth/session')
}
