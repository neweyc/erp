import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { SessionProvider } from '../components/session-provider'
import type { Session } from '../lib/session'
import { Departments } from './departments'
import { JobTitles } from './job-titles'

const admin: Session = {
  userId: 'usr_admin',
  email: 'admin@acme.test',
  roles: [{ key: 'core:admin', name: 'Administrator' }],
  permissions: ['core.employee.read', 'core.catalog.manage'],
  tenantName: 'Acme',
  licensedApps: [],
  tenantSuspended: false,
}
const reader: Session = { ...admin, roles: [{ key: 'core:member', name: 'Member' }], permissions: ['core.employee.read'] }

const departments = [
  { departmentId: 'dept_eng', name: 'Engineering', parentId: null, depth: 1, path: 'Engineering', active: true },
  { departmentId: 'dept_plat', name: 'Platform', parentId: 'dept_eng', depth: 2, path: 'Engineering › Platform', active: true },
  { departmentId: 'dept_tools', name: 'Tools', parentId: 'dept_plat', depth: 3, path: 'Engineering › Platform › Tools', active: true },
  { departmentId: 'dept_sales', name: 'Sales', parentId: null, depth: 1, path: 'Sales', active: true },
  { departmentId: 'dept_old', name: 'Old division', parentId: null, depth: 1, path: 'Old division', active: false },
]

type Handler = (init?: RequestInit) => { status?: number; body: unknown }

function stubApi(session: Session, handlers: Record<string, Handler>) {
  const routes: Record<string, Handler> = { 'GET /api/core/v1/auth/session': () => ({ body: session }), ...handlers }
  const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const handler = routes[`${(init?.method ?? 'GET').toUpperCase()} ${String(input)}`]
    const { status = 200, body } = handler ? handler(init) : { status: 404, body: { problemCode: 'not_found' } }
    return Promise.resolve({ ok: status < 400, status, json: async () => body } as Response)
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

const sent = (fetchMock: ReturnType<typeof stubApi>, method: string, path: string) =>
  fetchMock.mock.calls.filter(([url, init]) => String(url) === path && init?.method === method).map(([, init]) => JSON.parse(String(init!.body)))

function renderPage(page: React.ReactNode) {
  render(
    <SessionProvider>
      <MemoryRouter>{page}</MemoryRouter>
    </SessionProvider>,
  )
}

afterEach(() => vi.unstubAllGlobals())

describe('departments page', () => {
  it('shows the tree in order, each department named by its path', async () => {
    stubApi(admin, { 'GET /api/core/v1/departments': () => ({ body: departments }) })
    renderPage(<Departments />)

    const cells = await screen.findAllByRole('cell', { name: /Engineering|Sales|Old division/ })
    expect(cells.map((c) => c.getAttribute('aria-label'))).toEqual([
      'Engineering', 'Engineering › Platform', 'Engineering › Platform › Tools', 'Sales', 'Old division',
    ])
    expect(within(screen.getByRole('row', { name: /Old division/ })).getByText('Inactive')).toBeInTheDocument()
  })

  it('adds a department inside another', async () => {
    const fetchMock = stubApi(admin, {
      'GET /api/core/v1/departments': () => ({ body: departments }),
      'POST /api/core/v1/departments': () => ({ body: { departmentId: 'dept_new' } }),
    })
    const user = userEvent.setup()
    renderPage(<Departments />)

    await user.click(await screen.findByRole('button', { name: 'Add a department inside Engineering › Platform' }))
    expect(screen.getByLabelText('Inside')).toHaveDisplayValue('Engineering › Platform')
    await user.type(screen.getByLabelText('Name'), 'Data')
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Add department' }))

    expect(await screen.findByText('Data is added.')).toBeInTheDocument()
    expect(sent(fetchMock, 'POST', '/api/core/v1/departments')).toEqual([{ name: 'Data', parentId: 'dept_plat' }])
  })

  it('never offers to move a department inside itself, its own branch, or a retired one', async () => {
    stubApi(admin, { 'GET /api/core/v1/departments': () => ({ body: departments }) })
    const user = userEvent.setup()
    renderPage(<Departments />)

    await user.click(await screen.findByRole('button', { name: 'Edit Engineering' }))

    const offered = within(screen.getByLabelText('Inside')).getAllByRole('option').map((o) => o.textContent)
    expect(offered).toEqual(['Top level', 'Sales'])
  })

  it('retires a department and shows why the API refused', async () => {
    const fetchMock = stubApi(admin, {
      'GET /api/core/v1/departments': () => ({ body: departments }),
      'PUT /api/core/v1/departments/dept_eng': () => ({
        status: 400,
        body: { problemCode: 'validation_failed', message: 'It has active sub-departments. Deactivate or move them first.' },
      }),
    })
    const user = userEvent.setup()
    renderPage(<Departments />)

    await user.click(await screen.findByRole('button', { name: 'Edit Engineering' }))
    await user.click(screen.getByRole('checkbox', { name: /In use/ }))
    await user.click(screen.getByRole('button', { name: 'Save changes' }))

    expect(await within(screen.getByRole('dialog')).findByText(/It has active sub-departments/)).toBeInTheDocument()
    expect(sent(fetchMock, 'PUT', '/api/core/v1/departments/dept_eng')).toEqual([{ name: 'Engineering', parentId: null, active: false }])
  })

  it('is read-only for someone who cannot manage the lists', async () => {
    stubApi(reader, { 'GET /api/core/v1/departments': () => ({ body: departments }) })
    renderPage(<Departments />)

    await screen.findByRole('cell', { name: 'Sales' })
    expect(screen.queryByRole('button', { name: /Add|Edit/ })).not.toBeInTheDocument()
  })
})

describe('job titles page', () => {
  const titles = [
    { jobTitleId: 'title_eng', name: 'Engineer', active: true },
    { jobTitleId: 'title_old', name: 'Clerk', active: false },
  ]

  it('adds a job title, and explains a name already taken', async () => {
    const fetchMock = stubApi(admin, {
      'GET /api/core/v1/job-titles': () => ({ body: titles }),
      'POST /api/core/v1/job-titles': () => ({ status: 409, body: { problemCode: 'catalog_name_taken' } }),
    })
    const user = userEvent.setup()
    renderPage(<JobTitles />)

    await user.click(await screen.findByRole('button', { name: 'Add job title' }))
    await user.type(screen.getByLabelText('Name'), 'clerk')
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Add job title' }))

    expect(await within(screen.getByRole('dialog')).findByText(/reactivate it instead/)).toBeInTheDocument()
    expect(sent(fetchMock, 'POST', '/api/core/v1/job-titles')).toEqual([{ name: 'clerk' }])
  })

  it('renames and reactivates in one edit', async () => {
    const fetchMock = stubApi(admin, {
      'GET /api/core/v1/job-titles': () => ({ body: titles }),
      'PUT /api/core/v1/job-titles/title_old': () => ({ body: { jobTitleId: 'title_old' } }),
    })
    const user = userEvent.setup()
    renderPage(<JobTitles />)

    await user.click(await screen.findByRole('button', { name: 'Edit Clerk' }))
    await user.clear(screen.getByLabelText('Name'))
    await user.type(screen.getByLabelText('Name'), 'Office clerk')
    await user.click(screen.getByRole('checkbox', { name: /In use/ }))
    await user.click(screen.getByRole('button', { name: 'Save changes' }))

    expect(await screen.findByText('Office clerk is saved.')).toBeInTheDocument()
    expect(sent(fetchMock, 'PUT', '/api/core/v1/job-titles/title_old')).toEqual([{ name: 'Office clerk', active: true }])
  })
})
