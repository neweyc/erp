import type { ComponentType, LazyExoticComponent } from 'react'
import { lazy } from 'react'
import { BookOpen, Ticket } from 'lucide-react'
import { Permissions, type AppProps, type Permission } from '@app-platform/web-api'

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
  /** One line saying what the app is for, on the dashboard's card for it. */
  readonly description: string
  /** Shown beside the name in the navigation and on the dashboard. */
  readonly icon: ComponentType<{ className?: string }>
  /** Route prefix. Every route the app owns lives under it. */
  readonly path: string
  /**
   * The permission needed to see the app at all — in practice its read permission, which every
   * one of its roles grants. Hidden, never disabled: a disabled control advertises a capability
   * the reader cannot have and invites them to ask for it.
   */
  readonly permission: Permission
  /**
   * Deferred on purpose. Held as a function so the module is fetched only when an entitled
   * user actually routes to it: an unlicensed app's code must never reach the browser, both
   * because it is wasted bytes and because the bundle would advertise features nobody bought.
   */
  readonly load: () => Promise<{ default: ComponentType<AppProps> }>
}

/** Always on for every tenant, so it carries no entitlement and no registry entry. */
export const CORE_APP_ID = 'core'

export const APPS: readonly AppDefinition[] = [
  {
    id: 'tickets',
    name: 'Tickets',
    description: 'Raise, assign and close requests for help.',
    icon: Ticket,
    path: '/tickets',
    permission: Permissions.tickets.ticketRead,
    load: () => import('@app-platform/tickets-ui'),
  },
  {
    id: 'ledger',
    name: 'Ledger',
    description: 'Post journal entries, read the trial balance, close the books.',
    icon: BookOpen,
    path: '/ledger',
    permission: Permissions.ledger.read,
    load: () => import('@app-platform/ledger-ui'),
  },
]

/**
 * What this viewer may open: licensed to the tenant AND readable with their permissions.
 *
 * Both gates are cosmetic. The APIs enforce entitlement and permission independently and answer
 * 403; the shell only decides what to show. Treat anything decided here as a convenience, never
 * as access control.
 */
export function visibleApps(
  licensedApps: readonly string[],
  permissions: readonly string[],
): AppDefinition[] {
  return APPS.filter((app) => licensedApps.includes(app.id) && permissions.includes(app.permission))
}

const componentCache = new Map<string, LazyExoticComponent<ComponentType<AppProps>>>()

/**
 * Cached so a re-render does not re-enter the loader. React.lazy holds its own promise, but a
 * fresh lazy() on every render creates a fresh component type and remounts the app — losing
 * whatever the user had typed.
 */
export function componentFor(app: AppDefinition): LazyExoticComponent<ComponentType<AppProps>> {
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
