/** One row of GET /api/core/v1/departments, which arrive in tree order. */
export interface DepartmentRow {
  readonly departmentId: string
  readonly name: string
  readonly parentId: string | null
  /** 1 at the top. */
  readonly depth: number
  /** "Engineering › Platform". */
  readonly path: string
  readonly active: boolean
}

/** One row of GET /api/core/v1/job-titles or /locations: the two flat lists share a shape. */
export interface CatalogRow {
  readonly id: string
  readonly name: string
  readonly active: boolean
}

/** Where an employee sits, as GET /api/core/v1/employees returns each part (null for none). */
export interface Placement {
  readonly id: string
  /** For a department, its path. */
  readonly name: string
  readonly active: boolean
}

/** A choice in a placement picker. */
export interface PickerOption {
  readonly id: string
  readonly label: string
}

/**
 * What a placement picker offers: every item in use, plus the one the employee already has even if
 * it has since been retired, so editing their name does not silently move them out of it. The API
 * applies the same rule (EmployeePlacement); this only keeps the form from offering what it refuses.
 *
 * Pick-only, never free text: a typo must dead-end at "not in the list", not become a new department.
 */
export function pickerOptions(items: readonly CatalogRow[], current: string | null): PickerOption[] {
  return items
    .filter((item) => item.active || item.id === current)
    .map((item) => ({ id: item.id, label: item.active ? item.name : `${item.name} (inactive)` }))
}

/** Departments as picker rows: their path is the name, so siblings elsewhere that share a name stay distinct. */
export function departmentItems(departments: readonly DepartmentRow[]): CatalogRow[] {
  return departments.map((d) => ({ id: d.departmentId, name: d.path, active: d.active }))
}

/**
 * The departments a department may be moved under: active ones, minus itself and everything inside
 * it (the API refuses a loop too; this keeps the picker from offering one).
 */
export function possibleParents(departments: readonly DepartmentRow[], moving: string | null): DepartmentRow[] {
  if (!moving) return departments.filter((d) => d.active)
  const excluded = new Set([moving])
  // Tree order puts every sub-department after its parent, so one pass finds the whole branch.
  for (const d of departments) if (d.parentId && excluded.has(d.parentId)) excluded.add(d.departmentId)
  return departments.filter((d) => d.active && !excluded.has(d.departmentId))
}
