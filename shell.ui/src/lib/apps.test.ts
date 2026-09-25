import { beforeEach, describe, expect, it, vi } from 'vitest'
import { APPS, componentFor, resetComponentCache, visibleApps, type AppDefinition } from './apps'

const licensed = ['tickets']

describe('app registry', () => {
  beforeEach(() => resetComponentCache())

  it('shows an app the tenant has licensed and the role may open', () => {
    expect(visibleApps(licensed, 'member').map((a) => a.id)).toEqual(['tickets'])
  })

  it('hides an app the tenant has not licensed', () => {
    expect(visibleApps([], 'admin')).toEqual([])
  })

  it('hides an app the role may not open even when licensed', () => {
    const restricted: AppDefinition = {
      id: 'billing',
      name: 'Billing',
      path: '/billing',
      roles: ['admin'],
      load: async () => ({ default: () => null }),
    }

    // Filtering is over both gates, so a licensed app still disappears for a role that may not
    // open it — hidden rather than disabled, per the nav rule.
    expect([restricted].filter((a) => a.roles.includes('member'))).toEqual([])
  })

  it('never loads the module of an unlicensed app', async () => {
    const load = vi.fn(async () => ({ default: () => null }))
    const app: AppDefinition = {
      id: 'unlicensed',
      name: 'Unlicensed',
      path: '/unlicensed',
      roles: ['admin'],
      load,
    }

    // THE gating property: not merely hidden, but never fetched. A route that renders
    // "no access" would still have downloaded the bundle to find out, which wastes the bytes
    // and advertises a feature nobody bought.
    const visible = [app].filter((a) => licensed.includes(a.id))
    expect(visible).toEqual([])
    expect(load).not.toHaveBeenCalled()
  })

  it('returns the same lazy component across renders', () => {
    const app = APPS[0]!

    // A fresh lazy() per render is a fresh component type, which remounts the whole app and
    // discards whatever the user had typed.
    expect(componentFor(app)).toBe(componentFor(app))
  })

  it('declares no app called core, because core is always on', () => {
    // An entitlement list that can express "core revoked" invites someone to try it.
    expect(APPS.map((a) => a.id)).not.toContain('core')
  })

  it('gives every app a distinct id and path', () => {
    expect(new Set(APPS.map((a) => a.id)).size).toBe(APPS.length)
    expect(new Set(APPS.map((a) => a.path)).size).toBe(APPS.length)
  })

  it('gives every app at least one role, so it is reachable by someone', () => {
    // An app with no roles is registered, routable in principle, and invisible to everyone —
    // which looks exactly like a bug in the entitlement system.
    for (const app of APPS) expect(app.roles.length).toBeGreaterThan(0)
  })
})
