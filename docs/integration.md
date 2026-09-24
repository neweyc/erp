# Integration surface

**Status:** design, v0.1 — September 24, 2026. Three decisions below are needed in
Milestone 1; everything else is deliberately deferred.

## 1. The strategy, and its bill

Integrations create switching cost. A customer whose payroll system writes employees into
core, and whose phone system raises tickets, does not migrate away casually. That is
real, and it is the strongest retention mechanism a small product has.

It is also a **permanent obligation**. Internal contracts get refactored; a published one
cannot. You cannot grep your customers' code. Every integration is additionally a support
surface — "your webhook didn't fire" becomes yours to disprove, with their logs, at their
convenience.

So the position this document takes:

> **Decide now what is irreversible. Build when a customer asks.**

Integration value is mostly *retention*, and retention needs customers. Building a broad
integration platform before the first one is speculation with a maintenance bill. But
three things cannot be retrofitted cheaply, and skipping them now is expensive later.

## 2. The three decisions that cannot wait

### 2.1 Public ids, from the first migration

Anything that will ever be referenced from outside carries a **stable, opaque public id**
in its own column with a unique index — separate from the primary key.

```
emp_01JQ8Z3K2M4P6R8T0V2X4Y6A8C      tickets_ticket -> tkt_...
```

Sequential integers leak row counts and growth rate across tenants, cannot be re-keyed,
and tie the external contract to a storage decision. The prefix is not decoration: it
makes a misrouted id obvious in a log and stops a ticket id being accepted where an
employee id was meant.

The public id is what appears in API responses, webhook payloads, and CSV exports. The
internal key never leaves the database. **This is the one item on the list that is
genuinely irreversible** — once a customer stores our ids, they are permanent.

### 2.2 Every domain write emits an event, even with no consumers

The outbox exists already for email. Domain events go in the same table, with no
transport attached until someone subscribes.

The reason is asymmetric cost: adding emission later gives you **no history**. You cannot
retroactively emit `employee.hired` for people hired before the feature shipped, so the
first integrating customer gets a system with amnesia at exactly the moment they are
deciding whether to trust it. Emitting from day one costs a row per write.

Events are pruned on a retention window, not kept forever.

### 2.3 API keys are a principal, not a user

An integration credential is its own kind of principal with its own table, scopes, audit
trail, and revocation — **never a user row flagged as a service account**. Machine
identities modelled as users end up in employee lists, get invited to things, consume
seats, and inherit a permission model designed for humans.

This changes `packages/auth` — which is why it belongs in Milestone 1 rather than being
discovered after the auth package is written. Concretely: two authentication schemes
(cookie for browsers, bearer key for machines) resolving to one authorization model, and
every policy check asking "what may this principal do" rather than "what may this user
do".

## 3. Public API: a curated surface, not the internal routes

This repo already has the pattern twice — `core_v1` views publish a contract separate
from core's tables; route version segments publish one separate from internal handlers.
The public API is the same idea at the outer edge, and the harshest instance of it.

- Mounted at `/api/public/v1/`, **deliberately narrow**, mapped from internal features.
  Internal routes are never quietly promoted to public: the moment a route is public, its
  shape is frozen.
- Additive change only. Breaking means `v2` alongside `v1`, with a deprecation window
  measured in quarters, not releases.
- Never exposes an encrypted column, an internal id, or a field whose meaning depends on
  UI context.
- OpenAPI generated from the surface and published. A hand-maintained spec drifts.
- Scoped per app and per entitlement: `tickets:read` is refused if the tenant has not
  licensed tickets. Integration scopes never route around the entitlement model.

## 4. Outbound events (webhooks)

The mechanism already exists. `packages/outbox` was chosen for email because a record and
its notification must commit together; a webhook is the same problem with a different
transport. **One reliable-delivery mechanism, two transports** — not a second system.

- **Delivery is at-least-once.** Every event carries a stable `event_id` and the docs say
  plainly that consumers must be idempotent. Exactly-once is not offered because it
  cannot be honestly provided.
- **No global ordering promise.** Each event carries a per-aggregate sequence number so a
  consumer can detect gaps and reorder what it cares about. Promising ordered delivery is
  expensive and usually a lie.
- **Payloads are thin and carry no PII.** Public ids, event type, timestamp, sequence,
  and a small number of stable non-sensitive fields; the consumer calls back for detail
  under its own scopes. A fat payload ships employee data to whatever URL a customer
  typed into a form, and the encryption and boundary rules elsewhere in this repo would
  be pointless if the event feed posted the same data in plaintext.
- **Signed**: HMAC over the raw body with a per-endpoint secret, plus a timestamp, and a
  documented verification recipe. Rotation supported by accepting two secrets during a
  window.
- **Retry with backoff, dead-letter, and auto-disable** after sustained failure, with the
  state visible to the customer in the UI — not discovered through a support ticket.
- **Replay from a retention window**, self-service. This is the single highest-leverage
  DX feature: it turns "we missed events during our outage" from a support escalation
  into a button.

### SSRF is the real risk here

A customer-supplied URL is an instruction to make a server-side request from inside our
network. Mandatory, not optional: resolve and block private, loopback, link-local, and
multicast ranges — **including `169.254.169.254`, the cloud metadata endpoint** — re-check
after every redirect rather than only the first URL, cap redirects, cap response size,
and set an aggressive timeout. Egress from the webhook worker should be restricted at the
network level as well, because a code-level check is one bug from being bypassed.

## 5. Inbound (consuming their events) — deferred, deliberately

Harder than outbound and worth less early. When it comes:

- Inbound writes go through the **same command handlers as the UI**. A separate write
  path means two sets of validation and one of them is wrong — usually the one without a
  human watching.
- Idempotency key required on every write; a replay returns the original result.
- Rate limited per tenant and per key.
- Partial-failure semantics stated: what a batch of 500 does when row 200 is invalid.

A read API plus outbound webhooks covers most of the embedding value. Do those first.

## 6. Also "integration", and often what is actually wanted

Worth checking which of these a prospect means before building any of the above.

- **SSO** (OIDC/SAML) — frequently the real ask, and higher value per hour than a data API.
- **SCIM** provisioning — employees created and deactivated from their directory.
- **Scheduled export** to SFTP or object storage. Unglamorous; often sufficient.
- **Embedded UI** — a signed, scoped iframe or widget. Cheap embedding, no data contract.

## 7. Self-service diagnostics, or it becomes your evenings

Integration support cost is dominated by "did you receive it?". A **per-tenant integration
log** — what was sent, when, the response code, the response body, the retry state,
replayable — converts most of that into something the customer answers themselves. Build
it with the first webhook, not after the first support escalation.

## 8. Open

- **Is API access licensed?** Charging for integration is standard and it is also a
  friction that works against the embedding strategy. Undecided.
- Rate limits, quotas, and fair-use for the public API.
- Whether a partner/marketplace tier ever exists, or integrations stay customer-built.
- Sandbox tenants for customer development.
- Whether events are exposed as a pollable feed as well as pushed, for consumers who
  cannot host an endpoint.
