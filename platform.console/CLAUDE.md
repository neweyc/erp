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
