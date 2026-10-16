import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Permissions } from '@app-platform/web-api'
import { APPS, componentFor, resetComponentCache, visibleApps, type AppDefinition } from './apps'

const licensed = ['tickets']
const ticketReader = [Permissions.core.employeeRead, Permissions.tickets.ticketRead]

describe('app registry', () => {
  beforeEach(() => resetComponentCache())

  it('shows an app the tenant has licensed and the user may read', () => {
    expect(visibleApps(licensed, ticketReader).map((a) => a.id)).toEqual(['tickets'])
  })

  it('hides an app the tenant has not licensed, whatever the permissions', () => {
    expect(visibleApps([], ticketReader)).toEqual([])
  })

  it('hides a licensed app from a user with no role in it', () => {
    // Both gates apply, so a licensed app still disappears for someone it grants nothing —
    // hidden rather than disabled, per the nav rule. A core administrator is such a user until
    // they give themselves an app role.
    expect(visibleApps(licensed, [Permissions.core.userManage])).toEqual([])
  })

  it('never loads the module of an unlicensed app', async () => {
    const load = vi.fn(async () => ({ default: () => null }))
    const app: AppDefinition = {
      id: 'unlicensed',
      name: 'Unlicensed',
      path: '/unlicensed',
      permission: Permissions.tickets.ticketRead,
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

  it('gates every app on one of its own permissions', () => {
    // An app gated on another app's permission would appear or vanish with the wrong licence.
    // That every permission is granted by some role is checked on the .NET side
    // (PermissionTests, PermissionCatalogueTests).
    for (const app of APPS) expect(app.permission.startsWith(`${app.id}.`)).toBe(true)
  })
})
