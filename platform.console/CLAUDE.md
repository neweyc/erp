# platform.console — operator SPA

Separate deployable on its own hostname, localhost-bound in compose. Talks only to
`platform.api`. Root rules in `../CLAUDE.md` apply.

- **Coverage is a hard gate**: `npm run test` fails below 100% line coverage. Narrow
  exclusions only (bootstrap, shadcn primitives). Same rule as EMS's console, for the
  same reason — the operator surface is where a silent bug is discovered by a customer.
- Never renders customer business data. If a screen here would show an employee's name,
  the design is wrong.
- Destructive operator actions (suspend, retire, rotate) confirm by typing the tenant
  name, and say in plain language what the tenant will experience.
- Every screen states *when* the data was read. Operator screens are trusted to be live
  and quietly go stale.

## What is built (Cycle 18)

Vite + React, the shared `ui-kit` and `web-api` client, served on its own origin and talking to
`platform.api` only (its dev proxy forwards `/api/platform` and nothing else). Pages:

- **Sign-in**: email, password and authenticator code in one form; no session exists until all
  three pass. Problem codes are explained, never matched on message text.
- **Tenants**: every tenant with status, address and licensed apps; **New tenant** creates one through
  core's provisioning, with an address suggested from the name and shown in full before it is used. The idempotency key is made once per opening of the dialog, so pressing Create again
  after a timeout returns the tenant the first attempt made, and the dialog cannot be closed while a
  submit is pending (reopening would make a new key).
- **A tenant**: where its people sign in, its former addresses and **Change address** (which says the
  old address stops working at once for anyone signed out); suspend, resume, retire (suspend and
  retire are typed-name confirmations that say what the customer's people will experience), license
  and revoke apps, the license history, and the operator actions taken on it.
- **Errors**: the error feed. **Audit log**: the 100 most recent operator actions, failed sign-ins
  included.

Every page shows "Read at" with a Refresh button; only the newest read is shown, so a slow Refresh
cannot put stale data back. A 401 on any read returns to sign-in with the reason (idle, revoked).
Sign-out says "signed out" only once the server has revoked the session. Tenant names and public ids are the only customer-identifying values shown;
they are commercial records, not business data.

Tests: `npm run test --workspace @app-platform/console` runs Vitest with v8 coverage and fails below
100% lines. The browser journey is `e2e/specs/operator-console.spec.mjs`.
