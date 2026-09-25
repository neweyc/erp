import type { Entry } from './api'
import { fromMinor } from './money'
import { Scrolls } from './Scrolls'

/** "2026-7": the gapless number an auditor would cite, in the form the server uses in memos. */
const reference = (e: Pick<Entry, 'fiscalYear' | 'number'>) => `${e.fiscalYear}-${e.number}`

export function JournalSection({
  entries, onReverse,
}: {
  entries: readonly Entry[]
  onReverse: (entryId: string) => Promise<boolean>
}) {
  const byId = new Map(entries.map((e) => [e.entryId, e]))

  return (
    <section aria-labelledby="journal-heading">
      <h2 id="journal-heading">Journal</h2>

      {entries.length === 0 ? (
        <p>Nothing posted yet.</p>
      ) : (
        <ul aria-label="Journal entries">
          {entries.map((entry) => {
            const reverses = entry.reversesEntryId ? byId.get(entry.reversesEntryId) : undefined
            const reversedBy = entry.reversedByEntryId ? byId.get(entry.reversedByEntryId) : undefined

            return (
              <li key={entry.entryId}>
                <article aria-label={`Entry ${reference(entry)}`}>
                  <h3>{reference(entry)} · {entry.entryDate} · {entry.memo}</h3>

                  {/* Both directions, so an entry no longer in effect cannot be mistaken for a live one. */}
                  {entry.reversesEntryId && <p>Reverses {reverses ? reference(reverses) : 'an earlier entry'}.</p>}
                  {entry.reversedByEntryId && <p>Reversed by {reversedBy ? reference(reversedBy) : 'a later entry'}.</p>}

                  <Scrolls>
                    <table>
                      <thead><tr><th>Account</th><th>Debit</th><th>Credit</th></tr></thead>
                      <tbody>
                        {entry.lines.map((line) => (
                          <tr key={line.lineNumber}>
                            <td>{line.accountCode}</td>
                            <td>{line.amountMinor > 0 ? fromMinor(line.amountMinor) : ''}</td>
                            <td>{line.amountMinor < 0 ? fromMinor(-line.amountMinor) : ''}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </Scrolls>

                  {/* Offered only where the server would accept it: never twice, never a reversal. */}
                  {!entry.reversesEntryId && !entry.reversedByEntryId && (
                    <button type="button" onClick={() => void onReverse(entry.entryId)}>Reverse</button>
                  )}
                </article>
              </li>
            )
          })}
        </ul>
      )}
    </section>
  )
}
