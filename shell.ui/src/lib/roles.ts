import { APPS } from './apps'

/** One row of GET /api/core/v1/roles: a built-in role or one of the organization's own. */
export interface RoleRow {
  /** What a user's roles store: `ledger:controller` for a built-in role, `rol_…` for a custom one. */
  readonly key: string
  readonly name: string
  readonly description: string
  readonly builtIn: boolean
  readonly permissions: readonly string[]
  /** How many accounts hold it, invited ones included. */
  readonly holders: number
}

/** One row of GET /api/core/v1/permissions. */
export interface PermissionRow {
  readonly name: string
  readonly app: string
  readonly description: string
}

const CORE = 'core'

/** The display name of an app, with core called what a customer would call it. */
export function appTitle(app: string): string {
  if (app === CORE) return 'Organization'
  return APPS.find((a) => a.id === app)?.name ?? app
}

/**
 * Roles grouped for display: the built-in roles app by app (core first), then the organization's
 * own. The server already returns only core and licensed apps, so every group shown is usable.
 */
export function roleGroups(roles: readonly RoleRow[]): { title: string; roles: RoleRow[] }[] {
  const builtIn = roles.filter((r) => r.builtIn)
  const apps = [...new Set(builtIn.map((r) => r.key.slice(0, r.key.indexOf(':'))))].sort((a, b) =>
    a === CORE ? -1 : b === CORE ? 1 : a.localeCompare(b),
  )

  const groups = apps.map((app) => ({
    title: appTitle(app),
    roles: builtIn.filter((r) => r.key.startsWith(`${app}:`)),
  }))

  const custom = roles.filter((r) => !r.builtIn)
  if (custom.length > 0) groups.push({ title: "Your organization's roles", roles: custom })

  return groups
}
