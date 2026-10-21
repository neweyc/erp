import { useState, type ReactNode } from 'react'
import {
  flexRender, getCoreRowModel, getFilteredRowModel, getSortedRowModel, useReactTable,
  type ColumnDef, type SortingState,
} from '@tanstack/react-table'
import { ArrowDown, ArrowUp, ChevronsUpDown, Search } from 'lucide-react'
import { cn } from './cn'
import { Input } from './input'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from './table'

export type { ColumnDef } from '@tanstack/react-table'

/**
 * A table of records with sorting and, when `filterLabel` is given, a search box. The data is
 * already loaded; this only orders and narrows what is on screen.
 *
 * Columns are TanStack column definitions. A column sorts when it has an accessor; give
 * `enableSorting: false` to one that should not (actions). `meta` adds, per column:
 * - `className`, styling its header and cells;
 * - `cellLabel`, the words a cell's decorated content stands for ("Member, Clerk" for two role
 *   chips), given as its accessible name so a screen reader hears the sentence, not the parts.
 */
export interface ColumnMeta<Row> {
  className?: string
  cellLabel?: (row: Row) => string
}

export function DataTable<Row>({
  label, columns, data, getRowId, empty, filterLabel, toolbar,
}: {
  /** The table's accessible name. */
  label: string
  columns: ColumnDef<Row, any>[]
  data: readonly Row[]
  getRowId: (row: Row) => string
  /** Shown in place of rows when there are none at all. */
  empty: ReactNode
  /** "Search users": shows the search box, labeled with this. */
  filterLabel?: string
  /** Controls beside the search box. */
  toolbar?: ReactNode
}) {
  const [sorting, setSorting] = useState<SortingState>([])
  const [filter, setFilter] = useState('')

  const table = useReactTable({
    data: data as Row[],
    columns,
    getRowId,
    state: { sorting, globalFilter: filter },
    onSortingChange: setSorting,
    onGlobalFilterChange: setFilter,
    getCoreRowModel: getCoreRowModel(),
    getSortedRowModel: getSortedRowModel(),
    getFilteredRowModel: getFilteredRowModel(),
  })

  const rows = table.getRowModel().rows

  return (
    <div className="grid min-w-0">
      {(filterLabel || toolbar) && (
        <div className="flex flex-wrap items-center gap-2 border-b px-5 py-3 sm:px-6">
          {filterLabel && (
            <div className="relative w-full sm:max-w-xs">
              <Search aria-hidden className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground" />
              <Input
                type="search"
                aria-label={filterLabel}
                placeholder={`${filterLabel}…`}
                value={filter}
                onChange={(e) => setFilter(e.target.value)}
                className="pl-9"
              />
            </div>
          )}
          {toolbar && <div className="flex flex-wrap items-center gap-2 sm:ml-auto">{toolbar}</div>}
        </div>
      )}

      {data.length === 0 ? (
        empty
      ) : (
        <Table aria-label={label}>
          <TableHeader>
            {table.getHeaderGroups().map((group) => (
              <TableRow key={group.id} className="hover:bg-transparent">
                {group.headers.map((header) => {
                  const sorted = header.column.getIsSorted()
                  const meta = header.column.columnDef.meta as ColumnMeta<Row> | undefined
                  return (
                    <TableHead
                      key={header.id}
                      scope="col"
                      className={meta?.className}
                      aria-sort={sorted === 'asc' ? 'ascending' : sorted === 'desc' ? 'descending' : undefined}
                    >
                      {header.isPlaceholder ? null : header.column.getCanSort() ? (
                        <button
                          type="button"
                          onClick={header.column.getToggleSortingHandler()}
                          className="-ml-1 inline-flex items-center gap-1 rounded px-1 py-0.5 uppercase hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring/50 focus-visible:outline-none"
                        >
                          {flexRender(header.column.columnDef.header, header.getContext())}
                          {sorted === 'asc' ? (
                            <ArrowUp aria-hidden className="size-3.5" />
                          ) : sorted === 'desc' ? (
                            <ArrowDown aria-hidden className="size-3.5" />
                          ) : (
                            <ChevronsUpDown aria-hidden className="size-3.5 opacity-50" />
                          )}
                        </button>
                      ) : (
                        flexRender(header.column.columnDef.header, header.getContext())
                      )}
                    </TableHead>
                  )
                })}
              </TableRow>
            ))}
          </TableHeader>
          <TableBody>
            {rows.length === 0 ? (
              <TableRow className="hover:bg-transparent">
                <TableCell colSpan={columns.length} className="py-10 text-center text-muted-foreground">
                  Nothing matches “{filter}”.
                </TableCell>
              </TableRow>
            ) : (
              rows.map((row) => (
                <TableRow key={row.id}>
                  {row.getVisibleCells().map((cell) => {
                    const meta = cell.column.columnDef.meta as ColumnMeta<Row> | undefined
                    return (
                      <TableCell key={cell.id} className={cn(meta?.className)} aria-label={meta?.cellLabel?.(row.original)}>
                        {flexRender(cell.column.columnDef.cell, cell.getContext())}
                      </TableCell>
                    )
                  })}
                </TableRow>
              ))
            )}
          </TableBody>
        </Table>
      )}
    </div>
  )
}
