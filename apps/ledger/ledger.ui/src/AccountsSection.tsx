import { useState } from 'react'
import type { Account, AccountType } from './api'

const TYPES: readonly AccountType[] = ['Asset', 'Liability', 'Equity', 'Revenue', 'Expense']

export function AccountsSection({
  accounts, onCreate,
}: {
  accounts: readonly Account[]
  onCreate: (code: string, name: string, type: AccountType) => Promise<boolean>
}) {
  const [code, setCode] = useState('')
  const [name, setName] = useState('')
  const [type, setType] = useState<AccountType>('Asset')
  const [submitting, setSubmitting] = useState(false)

  async function submit(event: React.FormEvent) {
    event.preventDefault()
    setSubmitting(true)

    // Cleared only on success, so a refused account keeps what was typed.
    if (await onCreate(code.trim(), name.trim(), type)) {
      setCode('')
      setName('')
    }

    setSubmitting(false)
  }

  return (
    <section aria-labelledby="accounts-heading">
      <h2 id="accounts-heading">Chart of accounts</h2>

      {accounts.length === 0 ? (
        <p>No accounts yet. Add the first one below — nothing can be posted until there are two.</p>
      ) : (
        <ul aria-label="Accounts">
          {accounts.map((a) => (
            <li key={a.accountId}>
              {a.code} — {a.name} ({a.type})
            </li>
          ))}
        </ul>
      )}

      <form onSubmit={submit} aria-label="Add account">
        <label htmlFor="account-code">Code</label>
        <input id="account-code" value={code} onChange={(e) => setCode(e.target.value)} required maxLength={20} />

        <label htmlFor="account-name">Name</label>
        <input id="account-name" value={name} onChange={(e) => setName(e.target.value)} required maxLength={200} />

        <label htmlFor="account-type">Type</label>
        <select id="account-type" value={type} onChange={(e) => setType(e.target.value as AccountType)}>
          {TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
        </select>

        <button type="submit" disabled={submitting || code.trim() === '' || name.trim() === ''}>
          Add account
        </button>
      </form>
    </section>
  )
}
