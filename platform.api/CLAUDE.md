# platform.api — operator control plane

Owns the `platform` schema and the slim `tenant` table. Root rules in `../CLAUDE.md`
apply.

## The boundary is grants first, then the missing key

Say the guarantee precisely. The loose version — "the platform cannot read customer
data" — is **false**, and believing it is how a boundary quietly stops existing.

1. **Postgres grants are the boundary.** `platform.api` connects as a role granted only
   the `platform` schema and the slim `tenant` table. It has no SELECT on `core`,
   `identity`, or any app schema, so the connection is *unable* to read customer data
   whatever the code asks for. `docs/database-privileges.md`.
2. **The missing field key** protects encrypted columns only — PII, narratives,
   termination reasons. Employee names, emails, and ticket titles are **plaintext**, and
   the key protects none of them. The platform's own separate key encrypts operator TOTP
   secrets and nothing else.
3. **`PlatformDbContext` maps only `platform` + `tenant`,** and `BoundaryTests` enforces
   the whitelist, the no-reference-to-core rule, and a banned-strings source scan. This
   catches honest mistakes in this repo. It does not constrain raw SQL, so it is a
   guardrail, not the boundary.

Runtime and migration credentials are separate; the runtime role has no DDL.

Widening any of this is a design decision with a written record, never a convenience.

## What lives here

- **Tenants**: create, suspend, resume, retire. The console sets `tenant.status`; core
  and every app enforce it at sign-in and per request. A suspended tenant's admin keeps
  only auth and data-export routes, rejected without killing the cookie.
- **Entitlements**: which apps a tenant has licensed, with effective dates. This is the
  business model — see the root CLAUDE.md.
- **Billing records**: plans, terms, entitlement dates, activation authorization.
  Manual invoicing is acceptable; the platform records commercial state, it is not an
  accounting system.
- **Key rotation**: the console initiates and tracks a rotation. The re-encryption job
  runs inside the process that holds the key. **The console never sees the key or
  plaintext** — it sees a job id, a status, and counts.
- **Error feed**: Error+ occurrences mirrored from every process as **metadata only** —
  timestamp, fingerprint, app, tenant, count, a server-generated reference. There is no
  message or stack-trace column **by design**; do not add one without a new decision
  record. The sink is bounded, allowlisted per column, rate-limited per fingerprint, and
  never blocks, throws, or recurses.
- **Operator audit**: every operator action, in `platform.audit_log`, separate from
  tenant audit. Mutations audit explicitly and usually **staged**, so the action and its
  trail commit together. Post-commit audits are best-effort — a committed change is never
  reported as failed.

## Operator auth

Separate operator accounts, sessions, and tokens from tenant identity. **MFA is
mandatory**; first login walks enrollment. Accounts are created by CLI with the password
read from stdin, never a flag. Recovery is a CLI reset, not a self-service flow.

## Provisioning delegates

The platform does not write tenant business data. `POST` to core's internal endpoint
behind a shared secret creates tenant + company + invited admin + invite token in one
transaction, send-then-commit. Unset secret = 404, checked before the body is read; nginx
blocks the internal prefix publicly.
