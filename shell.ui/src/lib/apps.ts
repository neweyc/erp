import type { ComponentType, LazyExoticComponent } from 'react'
import { lazy } from 'react'

export type Role = 'admin' | 'manager' | 'member'

/**
 * One entry per app. This is the ONLY place an app is wired: nav, router, and permissions all
 * read from here.
 *
 * If adding an app ever means touching the nav *and* the router *and* a permissions map, the
 * registry is missing a field — that scattering is how an app ends up in the menu but not the
 * router, or routable but invisible.
 */
export interface AppDefinition {
  /** Matches the entitlement name the platform grants. */
  readonly id: string
  readonly name: string
  /** Route prefix. Every route the app owns lives under it. */
  readonly path: string
  /**
   * Roles that may see the app at all. Hidden, never disabled — a disabled control advertises
   * a capability the reader cannot have and invites them to ask for it.
   */
  readonly roles: readonly Role[]
  /**
   * Deferred on purpose. Held as a function so the module is fetched only when an entitled
   * user actually routes to it: an unlicensed app's code must never reach the browser, both
   * because it is wasted bytes and because the bundle would advertise features nobody bought.
   */
  readonly load: () => Promise<{ default: ComponentType }>
}

/** Always on for every tenant, so it carries no entitlement and no registry entry. */
export const CORE_APP_ID = 'core'

export const APPS: readonly AppDefinition[] = [
  {
    id: 'tickets',
    name: 'Tickets',
    path: '/tickets',
    roles: ['admin', 'manager', 'member'],
    load: () => import('@app-platform/tickets-ui'),
  },
  {
    id: 'ledger',
    name: 'Ledger',
    path: '/ledger',
    roles: ['admin', 'manager', 'member'],
    load: () => import('@app-platform/ledger-ui'),
  },
]

/**
 * What this viewer may open: licensed to the tenant AND permitted to the role.
 *
 * Both gates are cosmetic. The API enforces the entitlement independently and answers 403; the
 * shell only decides what to show. Treat anything decided here as a convenience, never as
 * access control.
 */
export function visibleApps(licensedApps: readonly string[], role: Role): AppDefinition[] {
  return APPS.filter((app) => licensedApps.includes(app.id) && app.roles.includes(role))
}

const componentCache = new Map<string, LazyExoticComponent<ComponentType>>()

/**
 * Cached so a re-render does not re-enter the loader. React.lazy holds its own promise, but a
 * fresh lazy() on every render creates a fresh component type and remounts the app — losing
 * whatever the user had typed.
 */
export function componentFor(app: AppDefinition): LazyExoticComponent<ComponentType> {
  let component = componentCache.get(app.id)

  if (!component) {
    component = lazy(app.load)
    componentCache.set(app.id, component)
  }

  return component
}

/** Test seam: the cache is module-level, so it survives between tests without this. */
export function resetComponentCache(): void {
  componentCache.clear()
}
