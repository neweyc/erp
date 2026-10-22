import { Checkbox, CheckboxRow } from '@app-platform/ui-kit'
import { type RoleRow, roleGroups } from '../lib/roles'

/**
 * Every role that can be given, grouped by app, each with its description: what the person is
 * actually choosing. Used wherever roles are given — editing someone's roles, inviting them.
 */
export function RoleChecklist({
  roles, chosen, onToggle,
}: {
  roles: readonly RoleRow[]
  chosen: ReadonlySet<string>
  onToggle: (key: string) => void
}) {
  return (
    <div className="grid gap-5">
      {roleGroups(roles).map((group) => (
        <fieldset key={group.title} className="grid gap-1">
          <legend className="mb-1.5 text-xs font-semibold tracking-wider text-muted-foreground uppercase">
            {group.title}
          </legend>
          {group.roles.map((role) => (
            <CheckboxRow
              key={role.key}
              control={<Checkbox checked={chosen.has(role.key)} onChange={() => onToggle(role.key)} />}
              title={role.name}
              description={role.description || 'No description.'}
            />
          ))}
        </fieldset>
      ))}
    </div>
  )
}

/** A copy of the set with the key added or removed. */
export function toggled(set: ReadonlySet<string>, key: string): ReadonlySet<string> {
  const next = new Set(set)
  if (next.has(key)) next.delete(key)
  else next.add(key)
  return next
}
