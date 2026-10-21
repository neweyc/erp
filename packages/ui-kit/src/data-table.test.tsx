import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { DataTable, type ColumnDef } from './data-table'

interface Person {
  id: string
  name: string
  roles: string[]
}

const people: Person[] = [
  { id: '1', name: 'Grace', roles: ['Agent'] },
  { id: '2', name: 'Ada', roles: ['Member', 'Clerk'] },
  { id: '3', name: 'Linus', roles: [] },
]

const columns: ColumnDef<Person>[] = [
  { id: 'name', header: 'Name', accessorFn: (p) => p.name },
  {
    id: 'roles',
    header: 'Roles',
    accessorFn: (p) => p.roles.join(', '),
    meta: { cellLabel: (p: Person) => p.roles.join(', ') || 'No roles' },
    cell: ({ row }) => row.original.roles.map((r) => <span key={r}>{r}</span>),
  },
]

const names = () =>
  screen.getAllByRole('row').slice(1).map((row) => within(row).getAllByRole('cell')[0]!.textContent)

describe('DataTable', () => {
  it('sorts by a column when its header is pressed, and says so', async () => {
    const user = userEvent.setup()
    render(<DataTable label="People" columns={columns} data={people} getRowId={(p) => p.id} empty="None" />)

    expect(names()).toEqual(['Grace', 'Ada', 'Linus'])

    await user.click(screen.getByRole('button', { name: 'Name' }))
    expect(names()).toEqual(['Ada', 'Grace', 'Linus'])
    expect(screen.getByRole('columnheader', { name: 'Name' })).toHaveAttribute('aria-sort', 'ascending')
  })

  it('narrows the rows to those matching the search, and says when none do', async () => {
    const user = userEvent.setup()
    render(
      <DataTable label="People" columns={columns} data={people} getRowId={(p) => p.id} empty="None" filterLabel="Search people" />,
    )

    await user.type(screen.getByRole('searchbox', { name: 'Search people' }), 'clerk')
    expect(names()).toEqual(['Ada'])

    await user.clear(screen.getByRole('searchbox', { name: 'Search people' }))
    await user.type(screen.getByRole('searchbox', { name: 'Search people' }), 'zzz')
    expect(screen.getByText('Nothing matches “zzz”.')).toBeInTheDocument()
  })

  it('names a decorated cell with the words it stands for', () => {
    render(<DataTable label="People" columns={columns} data={people} getRowId={(p) => p.id} empty="None" />)

    expect(screen.getByRole('cell', { name: 'Member, Clerk' })).toBeInTheDocument()
    expect(screen.getByRole('cell', { name: 'No roles' })).toBeInTheDocument()
  })

  it('shows its empty state, not an empty table, when there is no data', () => {
    render(<DataTable label="People" columns={columns} data={[]} getRowId={(p) => p.id} empty={<p>No people yet</p>} />)

    expect(screen.getByText('No people yet')).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
  })
})
