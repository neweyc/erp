import { Field, Input } from '@app-platform/ui-kit'
import { signInUrlFor } from '../lib/api'
import { useSession } from './session'

/**
 * A tenant's address, as an operator types it, with the full address it will be shown under so
 * they can read it back before it is used. platform.api decides whether it can be used.
 */
export function AddressField({
  id, value, onChange,
}: {
  id: string
  value: string
  onChange: (value: string) => void
}) {
  const { session } = useSession()
  const subdomain = value.trim().toLowerCase()

  return (
    <Field
      label="Address"
      htmlFor={id}
      hint={subdomain && session
        ? `People sign in at ${signInUrlFor(subdomain, session.tenantBaseUrl)}`
        : 'Letters, digits and hyphens, starting with a letter.'}
    >
      <Input
        id={id}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        maxLength={40}
        autoComplete="off"
        spellCheck={false}
        required
      />
    </Field>
  )
}
