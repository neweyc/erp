import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { SessionProvider } from '../components/session-provider'
import type { Session } from '../lib/session'
import { Employees } from './employees'

const admin: Session = {
  userId: 'usr_admin',
  email: 'admin@acme.test',
  roles: [{ key: 'core:admin', name: 'Administrator' }],
  permissions: [
    'core.employee.read', 'core.employee.create', 'core.employee.update', 'core.employee.terminate',
    'core.user.read', 'core.user.manage', 'core.role.manage',
  ],
  tenantName: 'Acme',
  licensedApps: [],
  tenantSuspended: false,
}

const employee = (id: string, first: string, last: string, email: string | null, status = 'Active') => ({
  employeeId: id, firstName: first, lastName: last, displayName: `${first} ${last}`, email, status,
  department: null, jobTitle: null, location: null,
})

const ada = employee('emp_ada', 'Ada', 'Lovelace', 'ada@acme.test')
const grace = employee('emp_grace', 'Grace', 'Hopper', 'grace@acme.test')
const linus = employee('emp_linus', 'Linus', 'Torvalds', null)

const roles = [
  { key: 'core:admin', name: 'Administrator', description: 'Manages people.', builtIn: true, permissions: [], holders: 1 },
  { key: 'core:member', name: 'Member', description: 'Sees the roster.', builtIn: true, permissions: [], holders: 1 },
]

/** Ada has an account (signing in at a different address); Grace and Linus do not. */
const graceAccount = { userId: 'usr_ada', email: 'ada.old@acme.test', status: 'Active', roles: ['core:member'], invitation: null, employeeId: 'emp_ada' }

type Handler = (init?: RequestInit) => { status?: number; body: unknown }

function stubApi(handlers: Record<string, Handler>) {
  const routes: Record<string, Handler> = {
    'GET /api/core/v1/auth/session': () => ({ body: admin }),
    'GET /api/core/v1/employees?includeTerminated=false': () => ({ body: [ada, grace, linus] }),
    'GET /api/core/v1/users': () => ({ body: [graceAccount] }),
    'GET /api/core/v1/roles': () => ({ body: roles }),
    'GET /api/core/v1/departments': () => ({ body: [] }),
    'GET /api/core/v1/job-titles': () => ({ body: [] }),
    'GET /api/core/v1/locations': () => ({ body: [] }),
    ...handlers,
  }
  const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const key = `${(init?.method ?? 'GET').toUpperCase()} ${String(input)}`
    const handler = routes[key]
    const { status = 200, body } = handler ? handler(init) : { status: 404, body: { problemCode: 'not_found' } }
    return Promise.resolve({ ok: status < 400, status, json: async () => body } as Response)
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

/**
 * Renders the page and waits until the accounts have loaded too. The roster arrives first; when the
 * accounts follow, the table's columns change and its cells are replaced, so a button found before
 * then is no longer the one on the page.
 */
async function renderEmployees({ withAccounts = true } = {}) {
  render(
    <SessionProvider>
      <MemoryRouter>
        <Employees />
      </MemoryRouter>
    </SessionProvider>,
  )
  if (withAccounts) await screen.findByText('Signs in as ada.old@acme.test')
  else await screen.findByRole('row', { name: /Ada Lovelace/ })
}

const sent = (fetchMock: ReturnType<typeof stubApi>, method: string, path: string) =>
  fetchMock.mock.calls.filter(([url, init]) => String(url) === path && init?.method === method).map(([, init]) => JSON.parse(String(init!.body)))

afterEach(() => vi.unstubAllGlobals())

describe('employees page', () => {
  it('shows who has access, and where someone signs in when it differs from the roster', async () => {
    stubApi({})
    await renderEmployees()

    const adaRow = await screen.findByRole('row', { name: /Ada Lovelace/ })
    // Two "Active" cells: her employment, and her account.
    expect(within(adaRow).getAllByRole('cell', { name: 'Active' })).toHaveLength(2)
    expect(within(adaRow).getByText('Signs in as ada.old@acme.test')).toBeInTheDocument()
    expect(within(screen.getByRole('row', { name: /Grace Hopper/ })).getByRole('cell', { name: 'No access' })).toBeInTheDocument()
  })

  it('offers access only to someone with an email address and no account', async () => {
    stubApi({})
    await renderEmployees()

    await screen.findByRole('row', { name: /Ada Lovelace/ })
    expect(screen.getByRole('button', { name: 'Give Grace Hopper access' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Give Ada Lovelace access' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Give Linus Torvalds access' })).not.toBeInTheDocument()
  })

  it('gives access with the roles chosen', async () => {
    const fetchMock = stubApi({ 'POST /api/core/v1/employees/emp_grace/invite': () => ({ body: {} }) })
    const user = userEvent.setup()
    await renderEmployees()

    await user.click(await screen.findByRole('button', { name: 'Give Grace Hopper access' }))
    await user.click(screen.getByRole('checkbox', { name: /Administrator/ }))
    await user.click(screen.getByRole('button', { name: 'Send invitation' }))

    expect(await screen.findByText('An invitation is on its way to grace@acme.test.')).toBeInTheDocument()
    expect(sent(fetchMock, 'POST', '/api/core/v1/employees/emp_grace/invite')).toEqual([
      { roles: ['core:member', 'core:admin'], email: 'grace@acme.test' },
    ])
  })

  it('adds an employee and invites them in one step', async () => {
    const fetchMock = stubApi({
      'POST /api/core/v1/employees': () => ({ body: { employeeId: 'emp_new' } }),
      'POST /api/core/v1/employees/emp_new/invite': () => ({ body: {} }),
    })
    const user = userEvent.setup()
    await renderEmployees()

    await user.click(await screen.findByRole('button', { name: 'Add employee' }))
    await user.type(screen.getByLabelText('First name'), 'Margaret')
    await user.type(screen.getByLabelText('Last name'), 'Hamilton')
    await user.type(screen.getByLabelText('Email'), 'margaret@acme.test')
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Add employee' }))

    expect(await screen.findByText('Margaret Hamilton is on the roster, and an invitation is on its way.')).toBeInTheDocument()
    expect(sent(fetchMock, 'POST', '/api/core/v1/employees')).toEqual([
      { firstName: 'Margaret', lastName: 'Hamilton', email: 'margaret@acme.test', departmentId: null, jobTitleId: null, locationId: null },
    ])
    expect(sent(fetchMock, 'POST', '/api/core/v1/employees/emp_new/invite')).toEqual([
      { roles: ['core:member'], email: 'margaret@acme.test' },
    ])
  })

  it('says so when the employee was added but the invitation was refused', async () => {
    stubApi({
      'POST /api/core/v1/employees': () => ({ body: { employeeId: 'emp_new' } }),
      'POST /api/core/v1/employees/emp_new/invite': () => ({ status: 409, body: { problemCode: 'email_in_use' } }),
    })
    const user = userEvent.setup()
    await renderEmployees()

    await user.click(await screen.findByRole('button', { name: 'Add employee' }))
    await user.type(screen.getByLabelText('First name'), 'Margaret')
    await user.type(screen.getByLabelText('Last name'), 'Hamilton')
    await user.type(screen.getByLabelText('Email'), 'margaret@acme.test')
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Add employee' }))

    expect(await screen.findByText(/Margaret Hamilton is on the roster, but the invitation was not sent/)).toBeInTheDocument()
  })

  it('sends nothing, and says why, when the address changed while the form was open', async () => {
    stubApi({ 'POST /api/core/v1/employees/emp_grace/invite': () => ({ status: 409, body: { problemCode: 'employee_changed' } }) })
    const user = userEvent.setup()
    await renderEmployees()

    await user.click(await screen.findByRole('button', { name: 'Give Grace Hopper access' }))
    await user.click(screen.getByRole('button', { name: 'Send invitation' }))

    expect(await within(screen.getByRole('dialog')).findByText(/Their email address changed while this was open/)).toBeInTheDocument()
  })

  it('keeps the form open, with the reason, when an edit is refused', async () => {
    stubApi({ 'PUT /api/core/v1/employees/emp_grace': () => ({ status: 409, body: { problemCode: 'email_in_use' } }) })
    const user = userEvent.setup()
    await renderEmployees()

    await user.click(await screen.findByRole('button', { name: 'Edit Grace Hopper' }))
    await user.clear(screen.getByLabelText('Email'))
    await user.type(screen.getByLabelText('Email'), 'ada@acme.test')
    await user.click(screen.getByRole('button', { name: 'Save changes' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/already has that email address/)
    expect(screen.getByLabelText('Email')).toHaveValue('ada@acme.test')
  })

  it('terminates only after confirming, and says their access ends', async () => {
    const fetchMock = stubApi({ 'POST /api/core/v1/employees/emp_ada/terminate': () => ({ body: {} }) })
    const user = userEvent.setup()
    await renderEmployees()

    await user.click(await screen.findByRole('button', { name: 'Terminate Ada Lovelace' }))
    expect(screen.getByRole('alertdialog')).toHaveTextContent(/signed out at once/)
    expect(fetchMock).not.toHaveBeenCalledWith('/api/core/v1/employees/emp_ada/terminate', expect.anything())

    await user.click(screen.getByRole('button', { name: 'Confirm: terminate Ada Lovelace' }))

    expect(await screen.findByText('Ada Lovelace is terminated.')).toBeInTheDocument()
  })

  it('shows terminated people only when asked', async () => {
    const fetchMock = stubApi({
      'GET /api/core/v1/employees?includeTerminated=true': () => ({ body: [ada, employee('emp_old', 'Old', 'Timer', null, 'Terminated')] }),
    })
    const user = userEvent.setup()
    await renderEmployees()

    await screen.findByRole('row', { name: /Ada Lovelace/ })
    await user.click(screen.getByRole('checkbox', { name: 'Show terminated' }))

    expect(await screen.findByRole('row', { name: /Old Timer/ })).toBeInTheDocument()
    await waitFor(() => expect(fetchMock).toHaveBeenCalledWith('/api/core/v1/employees?includeTerminated=true', expect.anything()))
  })

  it('shows terminated people when the box is ticked while a termination is still in flight', async () => {
    let finishTerminate: () => void = () => {}
    const terminating = new Promise<void>((resolve) => { finishTerminate = resolve })
    let done = false
    const adaTerminated = { ...ada, status: 'Terminated' }
    const routes = stubApi({
      'POST /api/core/v1/employees/emp_ada/terminate': () => {
        done = true
        return { body: {} }
      },
      // What the server holds: once terminated, Ada is listed only when terminated people are asked for.
      'GET /api/core/v1/employees?includeTerminated=false': () => ({ body: done ? [grace, linus] : [ada, grace, linus] }),
      'GET /api/core/v1/employees?includeTerminated=true': () => ({ body: [done ? adaTerminated : ada, grace, linus] }),
    })
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (String(input).endsWith('/terminate')) await terminating
      return routes(input, init)
    }))
    const user = userEvent.setup()
    await renderEmployees()

    await user.click(await screen.findByRole('button', { name: 'Terminate Ada Lovelace' }))
    await user.click(screen.getByRole('button', { name: 'Confirm: terminate Ada Lovelace' }))
    await user.click(screen.getByRole('checkbox', { name: 'Show terminated' }))
    finishTerminate()

    // The termination's own reload lands last, so it must ask for what the box says now.
    expect(await screen.findByText('Ada Lovelace is terminated.')).toBeInTheDocument()
    await waitFor(() => expect(screen.getByRole('row', { name: /Ada Lovelace/ })).toBeInTheDocument())
    expect(screen.getByRole('checkbox', { name: 'Show terminated' })).toBeChecked()
  })

  it('keeps the newest list when an older request answers last', async () => {
    let answerSlowRequest: () => void = () => {}
    const slow = new Promise<void>((resolve) => { answerSlowRequest = resolve })
    const old = employee('emp_old', 'Old', 'Timer', null, 'Terminated')
    const routes = stubApi({
      'GET /api/core/v1/employees?includeTerminated=true': () => ({ body: [ada, grace, linus, old] }),
    })
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (String(input).endsWith('includeTerminated=true')) await slow
      return routes(input, init)
    }))
    const user = userEvent.setup()
    await renderEmployees()

    // Ticked and unticked: the second (unticked) request answers first, then the first one.
    const unticked = () => routes.mock.calls.filter(([url]) => String(url).endsWith('includeTerminated=false')).length
    const before = unticked()
    const box = screen.getByRole('checkbox', { name: 'Show terminated' })
    await user.click(box)
    await user.click(box)
    await waitFor(() => expect(unticked()).toBe(before + 1)) // the ticked request is still held back
    answerSlowRequest()

    // The ticked request's answer is older than what the box now says, so it must not be shown.
    await new Promise((resolve) => setTimeout(resolve, 50))
    expect(screen.queryByRole('row', { name: /Old Timer/ })).not.toBeInTheDocument()
    expect(box).not.toBeChecked()
  })

  it('is a plain roster for someone who may only read it', async () => {
    stubApi({ 'GET /api/core/v1/auth/session': () => ({ body: { ...admin, permissions: ['core.employee.read'] } }) })
    await renderEmployees({ withAccounts: false })

    await screen.findByRole('row', { name: /Ada Lovelace/ })
    expect(screen.queryByRole('columnheader', { name: 'Access' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Add employee|Edit|Terminate|Give/ })).not.toBeInTheDocument()
  })

  describe('placement', () => {
    const departments = [
      { departmentId: 'dept_eng', name: 'Engineering', parentId: null, depth: 1, path: 'Engineering', active: true },
      { departmentId: 'dept_plat', name: 'Platform', parentId: 'dept_eng', depth: 2, path: 'Engineering › Platform', active: true },
      { departmentId: 'dept_old', name: 'Old division', parentId: null, depth: 1, path: 'Old division', active: false },
    ]
    const jobTitles = [{ jobTitleId: 'title_eng', name: 'Engineer', active: true }]
    const locations = [{ locationId: 'loc_hq', name: 'Head office', active: true }]
    const catalogs = {
      'GET /api/core/v1/departments': () => ({ body: departments }),
      'GET /api/core/v1/job-titles': () => ({ body: jobTitles }),
      'GET /api/core/v1/locations': () => ({ body: locations }),
    }

    it('shows where each person sits', async () => {
      const placed = {
        ...grace,
        jobTitle: { id: 'title_eng', name: 'Engineer', active: true },
        department: { id: 'dept_plat', name: 'Engineering › Platform', active: true },
        location: { id: 'loc_hq', name: 'Head office', active: true },
      }
      stubApi({ ...catalogs, 'GET /api/core/v1/employees?includeTerminated=false': () => ({ body: [ada, placed] }) })
      await renderEmployees()

      expect(screen.getByRole('cell', { name: 'Engineer in Engineering › Platform at Head office' })).toBeInTheDocument()
      expect(within(screen.getByRole('row', { name: /Ada Lovelace/ })).getByText('Not set')).toBeInTheDocument()
    })

    it('places someone when adding them, picking from the lists only', async () => {
      const fetchMock = stubApi({ ...catalogs, 'POST /api/core/v1/employees': () => ({ body: { employeeId: 'emp_new' } }) })
      const user = userEvent.setup()
      await renderEmployees()

      await user.click(await screen.findByRole('button', { name: 'Add employee' }))
      await user.type(screen.getByLabelText('First name'), 'Margaret')
      await user.type(screen.getByLabelText('Last name'), 'Hamilton')
      await user.click(screen.getByRole('checkbox', { name: /Send an invitation/ }))
      // A retired department is not offered to someone new.
      expect(within(screen.getByLabelText('Department')).queryByRole('option', { name: /Old division/ })).not.toBeInTheDocument()
      await user.selectOptions(screen.getByLabelText('Department'), 'Engineering › Platform')
      await user.selectOptions(screen.getByLabelText('Job title'), 'Engineer')
      await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Add employee' }))

      expect(await screen.findByText('Margaret Hamilton is on the roster.')).toBeInTheDocument()
      expect(sent(fetchMock, 'POST', '/api/core/v1/employees')).toEqual([
        { firstName: 'Margaret', lastName: 'Hamilton', email: '', departmentId: 'dept_plat', jobTitleId: 'title_eng', locationId: null },
      ])
    })

    it('keeps a retired department on offer for someone already in it', async () => {
      const inOld = { ...grace, department: { id: 'dept_old', name: 'Old division', active: false } }
      const fetchMock = stubApi({
        ...catalogs,
        'GET /api/core/v1/employees?includeTerminated=false': () => ({ body: [ada, inOld] }),
        'PUT /api/core/v1/employees/emp_grace': () => ({ body: { employeeId: 'emp_grace' } }),
      })
      const user = userEvent.setup()
      await renderEmployees()

      await user.click(screen.getByRole('button', { name: 'Edit Grace Hopper' }))
      expect(screen.getByLabelText('Department')).toHaveDisplayValue('Old division (inactive)')
      await user.type(screen.getByLabelText('First name'), 'e')
      await user.click(screen.getByRole('button', { name: 'Save changes' }))

      await screen.findByText('Gracee Hopper is updated.')
      expect(sent(fetchMock, 'PUT', '/api/core/v1/employees/emp_grace')[0]).toMatchObject({ departmentId: 'dept_old' })
    })
  })
})
