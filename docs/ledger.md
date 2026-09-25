# Ledger — the thin financial slice

The second licensed app. Like tickets it proves machinery, not a market: it exists to find out
whether the platform's primitives survive contact with money. `docs/backlog.md` ("What the tickets
exercise does and does not probe") lists what tickets cannot reach; this slice is built to reach
exactly those, and nothing more.

**It is not an accounting product.** No invoices, no AP/AR, no tax, no multi-currency, no reports
beyond a trial balance. Each is real; none proves anything the smallest ledger does not.

## Prerequisite: append-only rows (D10)

A posted journal is corrected by reversal, never by edit. That is enforced twice, the same two
layers that keep `audit_log` append-only, now generalised:

1. **Code.** An entity implementing `IAppendOnly` (packages/tenancy) can be inserted, never
   modified or deleted; `TenantedDbContext` refuses the save. `AuditEntry` is one.
2. **Grant.** Its table is listed in `02-grants.sql`, which revokes UPDATE (table and column),
   DELETE and TRUNCATE from every runtime role, and in `99-verify.sql`, which reports a
   regression. `BoundaryTests` fails if an `IAppendOnly` table is missing from either list, so the
   two layers cannot drift apart.

## What cycle 6 builds

| Piece | Rule it tests |
|---|---|
| **Accounts** — code, name, type (asset, liability, equity, revenue, expense), per company | Catalog data with a stable code; the type decides the normal balance and never changes |
| **Post a journal entry** — date, memo, currency, two or more lines | **Balanced**: lines sum to zero. Enforced by the handler AND by a deferred constraint trigger, so a bug, raw SQL, or another client cannot commit an unbalanced entry |
| **Money as integer minor units** (`amount_minor bigint`, signed: debit positive, credit negative) with a three-letter currency code on the entry | No floating point, no rounding. One currency per entry; no FX. The code has ISO 4217's *form* but is not checked against the list — which currencies are supported, and their decimal places, is open |
| **Posted means immutable** — entries and lines are `IAppendOnly` | Correction semantics: there is no edit or delete endpoint, and the database refuses one anyway |
| **Reverse an entry** — a new entry with every line negated, pointing at the original | The only correction. At most once per entry (unique index); a reversal cannot itself be reversed |
| **Gapless entry numbers** per company per calendar year | The contentious one under concurrency: a counter row locked in the posting transaction, so a rolled-back post releases its number. Proven with concurrent posts against real PostgreSQL |
| **Trial balance** as of a date | The aggregate read tickets never needed; debits equal credits by construction |
| **Company** from `core_v1.company` | The first real use of the company dimension: every account and entry carries `company_id`, resolved through the published view (resolve, never trust) |

Every write emits an outbox event; accounts and entries are audited; the app is licensed as
`ledger` and every endpoint requires it. **Posting requires an idempotency key**: a retry after a
lost response returns the entry already made, and a key reused for a different entry is refused.

## What the database guarantees

The handlers check everything first, so callers get problem codes. The database enforces the same
rules again, so they hold against anything that connects as the ledger's runtime role — a handler
bug, raw SQL, or code that is not in this repo:

| Rule | Mechanism |
|---|---|
| Sealed: once posted, no line can be added | The entry declares its line count (fixed — entries are append-only); a line must be written after its entry, with a number in 1..count (trigger), unique per entry (index) |
| Complete and balanced: exactly the declared lines, summing to zero | Deferred trigger at COMMIT. Once it has passed, the seal leaves no free slot, so forcing it to run early (`SET CONSTRAINTS … IMMEDIATE`) cannot let a later line through |
| Append-only: no UPDATE, DELETE or TRUNCATE of entries and lines | Grants, verified by `99-verify.sql` |
| One company per entry; accounts cannot change company once used | Foreign keys carrying `(tenant_id, company_id, …)` |
| Gapless numbering: each series is exactly 1..N | The counter advances only by one (trigger), every number it issues is used (deferred trigger), no entry carries an unissued number (deferred trigger), numbers are unique (index), and the counter cannot be deleted (grant) |
| The series is the entry date's year | Check constraint |
| A reversal exactly mirrors its original, in the same currency, dated no earlier, and is not itself a reversal; at most one per entry | Deferred trigger, queued by the reversal AND by every line inserted into either side, so forcing it early cannot be escaped; unique index |
| No amount without a negation (`long.MinValue`) | Check constraint |
| Nothing is posted in a closed period | Trigger on entry insert, holding a share lock on the company's books row; a close updates that row, so a post and a close cannot interleave. Entries key to the books row, so it always exists |
| A close only moves forward, and cannot be removed | Trigger on the books row; DELETE and TRUNCATE revoked |

**What it does not guarantee.** Tenant isolation between rows is the application's job, as it is
everywhere in this platform: a process holding the runtime role can read and write any tenant's
ledger rows. Every *reference* carries the tenant, so it cannot mix two tenants' rows, but it can
act on either.

## Period close (cycle 7)

A company's books are closed **through a date**. Afterwards nothing can be posted on or before it,
by any path. An administrator closes; the date must be before today and after the current close.

**There is no reopen.** A closed period stays exactly what was reported for it; a mistake found in it
is corrected by an entry dated in an open period — typically the reversal of the wrong entry, which
the rules already date no earlier than its original. The books row is audited, which records who
closed each period and when; each close also emits `books.period_closed`.

Who may close is checked in the handler (`admin`), because packages/auth has no role model yet.

## The UI (cycle 7)

`apps/ledger/ledger.ui`, one page in the shell: closed periods, chart of accounts, posting, the
journal with its reversals, and the trial balance. It exists to make the invariants *visible*:

- Amounts are typed in currency units and converted to minor units by **string arithmetic**, never
  floating point (`12.10 * 100` is 1209.999… in JavaScript). Two decimal places is the UI's one
  currency assumption, stated in `money.ts`.
- The post button enables only when the lines balance, and the form says by how much they do not.
- The idempotency key belongs to the filled-in form: a retry of the same submission sends the same
  key, so the server returns the entry it already posted.
- Reverse is offered only where the server would accept it; both directions of a reversal are shown.
- The browser journey (`e2e/specs/ledger.spec.mjs`) licenses the ledger as an operator, then posts,
  reverses, closes the books and watches a post into the closed period be refused.

## Deliberately deferred

- **Fiscal years that are not calendar years.** Numbering is per calendar year of the entry date.
- **Multi-company consolidation.** Each entry belongs to one company; nothing sums across them.

## Numbers and money, precisely

- `amount_minor` is a signed 64-bit integer in the currency's minor unit (cents for USD). Zero lines
  are refused: a line that moves nothing is a mistake.
- An entry's lines must sum to exactly zero. The handler refuses first, with a clear problem code;
  the trigger is the backstop and fires at COMMIT, so a multi-statement insert is judged whole.
- Entry numbers are dense: 1, 2, 3 … per (tenant, company, year), with no gaps even when posts fail
  or race. A gap in a legally numbered series has to be explained to an auditor; a lock does not.
- Balances and trial-balance totals are summed as `numeric`/`decimal`. Every line fits in 64 bits;
  a balance across many entries, or a column total across many accounts, need not.
