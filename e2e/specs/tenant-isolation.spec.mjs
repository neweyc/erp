import { expect, request, test } from '@playwright/test'
import {
  OPERATOR_EMAIL, OPERATOR_PASSWORD, OTHER_TENANT_NAME, otherTenantPublicId, waitForInvitation,
} from '../stack.mjs'

/**
 * A2: a second tenant, signed in for real, cannot read, change, or reference the first tenant's
 * data through the running services.
 *
 * Handler-level tests already prove the filter. What only this proves is that the filter is
 * actually in force in the deployed processes: that the tenant is taken from the SESSION on every
 * request, in a core.api and tickets.api that serve both tenants at once.
 *
 * That last point is why tenant B signs in through a separate core.api (5103, pinned to B — see
 * the config) but does everything else through the SAME core.api (5100) and tickets.api (5102)
 * as tenant A. Were B's requests served by its own processes, a pass would prove nothing about
 * isolation inside a shared one.
 */

const CORE = 'http://localhost:5100'
const OTHER_CORE_SIGN_IN = 'http://localhost:5103'
const TICKETS = 'http://localhost:5102'
const PLATFORM = 'http://localhost:5101'

const A = { email: 'admin@e2e.test', password: 'correct horse battery' }
const B = { email: 'admin@other.test', password: 'other correct horse battery' }

/** Tenant B's own employee, created over HTTP by B's admin. */
const GRACE = { firstName: 'Grace', lastName: 'Hopper', email: 'grace@other.test' }

/**
 * Signs in at the given core and returns a context holding that session and its CSRF token.
 * Each identity gets its own context, so no cookie can leak between tenants inside the test.
 */
async function signIn(signInBase, { email, password }) {
  const api = await request.newContext()

  const response = await api.post(`${signInBase}/api/core/v1/auth/sign-in`, {
    data: { email, password },
  })

  const csrf = (await api.storageState()).cookies.find((c) => c.name === 'ap_csrf')?.value
  return { api, csrf, status: response.status(), body: await response.text() }
}

async function signInOrFail(signInBase, credentials) {
  const session = await signIn(signInBase, credentials)
  expect(session.status, session.body).toBe(200)
  expect(session.csrf, 'sign-in must issue a csrf token').toBeTruthy()
  return session
}

async function json(response) {
  expect(response.status(), await response.text()).toBe(200)
  return response.json()
}

async function listTickets(session) {
  return json(await session.api.get(`${TICKETS}/api/tickets/v1/tickets?includeClosed=true`))
}

async function listEmployees(session) {
  return json(await session.api.get(`${CORE}/api/core/v1/employees`))
}

async function createTicket(session, data) {
  return session.api.post(`${TICKETS}/api/tickets/v1/tickets`, {
    headers: { 'X-CSRF-Token': session.csrf },
    data,
  })
}

test.describe('a second tenant', () => {
  let tenantA
  let tenantB
  let adaId
  let graceId
  let ticketOfA
  let ticketOfB

  /**
   * Brings tenant B to the same point tenant A reaches in the journey — invitation delivered and
   * accepted, signed in, tickets licensed by an operator, an employee on its roster — all through
   * the real paths. Then gives each tenant a ticket for the other to try to reach.
   *
   * Idempotent, because Playwright re-runs beforeAll in a fresh worker after any failure, and a
   * second acceptance of the same invitation is (correctly) refused.
   */
  test.beforeAll(async () => {
    const existing = await signIn(OTHER_CORE_SIGN_IN, B)
    await existing.api.dispose()

    if (existing.status !== 200) {
      const token = await waitForInvitation(B.email)
      const anonymous = await request.newContext()
      const accepted = await anonymous.post(`${CORE}/api/core/v1/auth/accept-invite`, {
        data: { token, password: B.password },
      })
      expect(accepted.status(), await accepted.text()).toBe(200)
      await anonymous.dispose()
    }

    tenantB = await signInOrFail(OTHER_CORE_SIGN_IN, B)
    tenantA = await signInOrFail(CORE, A)

    // Licensed by an operator, as tenant A was. Unlicensed, every tickets request from B would be
    // refused 403 app_not_licensed — and "B cannot see A's tickets" would pass without the tenant
    // filter ever being consulted.
    await licenseTicketsForTenantB()

    const rosterOfB = await listEmployees(tenantB)
    graceId = rosterOfB.find((e) => e.email === GRACE.email)?.employeeId

    if (!graceId) {
      const created = await tenantB.api.post(`${CORE}/api/core/v1/employees`, {
        headers: { 'X-CSRF-Token': tenantB.csrf },
        data: GRACE,
      })
      graceId = (await json(created)).employeeId
    }

    adaId = (await listEmployees(tenantA)).find((e) => e.email === 'ada@e2e.test')?.employeeId
    expect(adaId, 'the seed must have given tenant A an employee').toBeTruthy()

    const stamp = Date.now()
    ticketOfA = {
      title: `Tenant A only ${stamp}`,
      id: (await json(await createTicket(tenantA, {
        title: `Tenant A only ${stamp}`, assigneeEmployeeId: adaId,
      }))).ticketId,
    }
    ticketOfB = {
      title: `Tenant B only ${stamp}`,
      id: (await json(await createTicket(tenantB, { title: `Tenant B only ${stamp}` }))).ticketId,
    }
  })

  test.afterAll(async () => {
    await tenantA?.api.dispose()
    await tenantB?.api.dispose()
  })

  test('signs in to its own tenant only, and its session is honoured by the shared core', async () => {
    // Read from 5100 — the core pinned to tenant A. The session, not the process's pinned tenant,
    // decides whose data this is.
    const session = await json(await tenantB.api.get(`${CORE}/api/core/v1/auth/session`))
    expect(session.tenantName).toBe(OTHER_TENANT_NAME)
    expect(session.licensedApps).toContain('tickets')

    // Each tenant's credentials are unknown at the other's door. Sign-in looks the user up inside
    // the resolved tenant, so another tenant's valid password is just a wrong password.
    const aAtB = await signIn(OTHER_CORE_SIGN_IN, A)
    const bAtA = await signIn(CORE, B)
    await aAtB.api.dispose()
    await bAtA.api.dispose()

    expect(aAtB.status).toBe(403)
    expect(aAtB.body).toContain('invalid_credentials')
    expect(bAtA.status).toBe(403)
    expect(bAtA.body).toContain('invalid_credentials')
  })

  test("cannot see the other tenant's tickets", async () => {
    const seenByB = await listTickets(tenantB)
    const seenByA = await listTickets(tenantA)

    // Each sees its OWN ticket — without this the absence below could mean "the list is broken".
    expect(seenByB.map((t) => t.ticketId)).toContain(ticketOfB.id)
    expect(seenByA.map((t) => t.ticketId)).toContain(ticketOfA.id)

    expect(seenByB.map((t) => t.ticketId)).not.toContain(ticketOfA.id)
    expect(seenByB.map((t) => t.title)).not.toContain(ticketOfA.title)
    expect(seenByA.map((t) => t.ticketId)).not.toContain(ticketOfB.id)
  })

  test("cannot close or reassign the other tenant's ticket, even knowing its id", async () => {
    const close = await tenantB.api.post(`${TICKETS}/api/tickets/v1/tickets/${ticketOfA.id}/close`, {
      headers: { 'X-CSRF-Token': tenantB.csrf },
    })
    const reassign = await tenantB.api.post(
      `${TICKETS}/api/tickets/v1/tickets/${ticketOfA.id}/assignee`, {
        headers: { 'X-CSRF-Token': tenantB.csrf },
        data: { employeeId: graceId },
      })

    // 404, not 403: to tenant B the ticket does not exist, and a 403 would confirm that it does.
    expect(close.status(), await close.text()).toBe(404)
    expect(await close.text()).toContain('not_found')
    expect(reassign.status(), await reassign.text()).toBe(404)
    expect(await reassign.text()).toContain('not_found')

    // And nothing changed underneath: tenant A still sees it open and assigned to Ada.
    const stillA = (await listTickets(tenantA)).find((t) => t.ticketId === ticketOfA.id)
    expect(stillA).toMatchObject({ status: 'Open', assigneeDisplayName: 'Ada Lovelace' })

    // B CAN close a ticket of its own, so the 404 above is about WHOSE ticket — not a close path
    // that refuses tenant B everything. A separate ticket, so the shared ticketOfB stays open for
    // the assignment case.
    const own = (await json(await createTicket(tenantB, { title: `B closes its own ${Date.now()}` })))
      .ticketId
    const closedOwn = await tenantB.api.post(`${TICKETS}/api/tickets/v1/tickets/${own}/close`, {
      headers: { 'X-CSRF-Token': tenantB.csrf },
    })
    expect(closedOwn.status(), await closedOwn.text()).toBe(200)
    expect((await listTickets(tenantB)).find((t) => t.ticketId === own).status).toBe('Closed')
  })

  test("cannot reference the other tenant's employee", async () => {
    // The one defence here is resolve-never-trust: a ticket's assignee points at a published VIEW,
    // which Postgres cannot key to, so no foreign key would stop this if the lookup were unfiltered.
    const title = `Assigned across tenants ${Date.now()}`
    const created = await createTicket(tenantB, { title, assigneeEmployeeId: adaId })
    expect(created.status(), await created.text()).not.toBe(200)
    expect(await created.text()).toContain('assignee_not_found')
    expect((await listTickets(tenantB)).map((t) => t.title)).not.toContain(title)

    const assignee = (ticketId, employeeId) => tenantB.api.post(
      `${TICKETS}/api/tickets/v1/tickets/${ticketId}/assignee`, {
        headers: { 'X-CSRF-Token': tenantB.csrf },
        data: { employeeId },
      })

    const refused = await assignee(ticketOfB.id, adaId)
    expect(refused.status(), await refused.text()).not.toBe(200)
    expect(await refused.text()).toContain('assignee_not_found')

    // Checked BEFORE the positive control below, which would otherwise overwrite the evidence of
    // an assignment that was saved despite the refusal.
    const afterRefusal = (await listTickets(tenantB)).find((t) => t.ticketId === ticketOfB.id)
    expect(afterRefusal.assigneeDisplayName).toBeNull()

    // Assigning B's own employee works, so the refusal above is about WHOSE employee — not a
    // broken assignment path.
    const accepted = await assignee(ticketOfB.id, graceId)
    expect(accepted.status(), await accepted.text()).toBe(200)

    const own = (await listTickets(tenantB)).find((t) => t.ticketId === ticketOfB.id)
    expect(own.assigneeDisplayName).toBe('Grace Hopper')
  })

  test("cannot see the other tenant's employees", async () => {
    // Both read through 5100, one process, two sessions.
    const rosterOfA = (await listEmployees(tenantA)).map((e) => e.employeeId)
    const rosterOfB = (await listEmployees(tenantB)).map((e) => e.employeeId)

    expect(rosterOfA).toContain(adaId)
    expect(rosterOfB).toContain(graceId)
    expect(rosterOfB).not.toContain(adaId)
    expect(rosterOfA).not.toContain(graceId)
  })
})

/** An operator licenses tickets for tenant B over HTTP. A repeat grant (409) is already done. */
async function licenseTicketsForTenantB() {
  const operator = await request.newContext({ baseURL: PLATFORM })

  const signedIn = await operator.post('/api/platform/v1/auth/sign-in', {
    data: { email: OPERATOR_EMAIL, password: OPERATOR_PASSWORD },
  })
  expect(signedIn.status(), await signedIn.text()).toBe(200)
  const csrf = (await operator.storageState()).cookies.find((c) => c.name === 'ap_csrf')?.value

  const granted = await operator.post(
    `/api/platform/v1/tenants/${otherTenantPublicId()}/entitlements`, {
      headers: { 'X-CSRF-Token': csrf },
      data: { app: 'tickets', licensed: true },
    })

  const body = await granted.text()
  expect([200, 409], body).toContain(granted.status())
  if (granted.status() === 409) expect(body).toContain('app_already_licensed')

  await operator.dispose()
}
