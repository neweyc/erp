import { api } from '@app-platform/web-api'
export type Role = 'admin' | 'manager' | 'member'

export interface Session {
  readonly userId: string
  readonly email: string
  /** The core role. Shown to the user; never used to decide what to show — see permissions. */
  readonly role: Role
  /** App name to role name. An app with no entry gives this user nothing in it. */
  readonly appRoles: Readonly<Record<string, string>>
  /**
   * What this user may do, computed by the server from the same rule the APIs enforce. The shell
   * uses it to hide what the user cannot do; it never derives permissions from roles itself.
   */
  readonly permissions: readonly string[]
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
