# platform.api — operator control plane

Owns the `platform` schema and the slim `tenant` table. Root rules in `../CLAUDE.md`
apply.

## The boundary is the missing key

The platform process **never receives `Encryption:FieldKey`**, so it cannot read customer
PII — not "must not", *cannot*. Its own separate key encrypts only operator TOTP secrets.
`PlatformDbContext` maps only the `platform` schema plus `tenant`. `BoundaryTests`
enforces the whitelist, the no-reference-to-core rule, and a banned-strings source scan.

Widening any of that is a design decision with a written record, never a convenience.

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
