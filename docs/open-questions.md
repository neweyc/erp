# Open questions

Written down so they are not re-litigated from memory. Each needs a decision, an
experiment, or an explicit deferral — not a default.

## Product

- **Does anyone pay for this?** The September viability work landed on a bounded
  recovery-check *service*, not a platform, and explicitly found no validated demand for
  a catalog or managed delivery. This repo is a bet placed ahead of that evidence. Worth
  being honest about in planning: it is a build-first decision, deliberately taken.
- **Tickets proves machinery, not market.** Ticketing is the most commoditized category
  in software. It is the right *proving* app and a poor *product* bet. Decide before v1
  ships whether it is ever sold on its own merits, or only bundled.
- **What is the second app?** Reuse is only proven by an app unlike the first. The ERP
  modules (GL, AP/AR, procure-to-pay, inventory) are the stated future direction; nothing
  commits to ordering yet.
- **Is core free, cheap, or bundled?** The commercial shape of a required spine is
  undecided, and it changes how "required" feels to a buyer.

## Architecture

- **When does the event bus become necessary?** Trigger conditions should be named now:
  an app needing separate infrastructure, a cross-app workflow a customer pays for, or
  read volume the views cannot serve. Until one fires, `core_v1` views stand.
- **Multi-company activation.** The column ships in v1; the features (switcher, per-entity
  fiscal calendars, consolidation, intercompany) are unscoped and belong with the first
  ERP app.
- **Key rotation mechanics.** The console initiates and tracks; the job runs where the key
  lives. The handshake, progress reporting, and failure/resume semantics are unspecified.
- **Per-app vs shared file storage.** Currently one store with an app segment in the path.
  Revisit if an app needs a different retention or residency policy.
- **Shared packages need a publishing story.** Versioned dependencies are the rule; the
  feed, release process, and upgrade cadence are not chosen.
- **Does the platform need its own database eventually?** One cluster is right for one
  operator. It is also the single blast radius.

## Operations

- **Supported version matrix.** `compatibility.md` exists but is empty; the policy for how
  many versions back are supported has not been set.
- **Backup and restore across schemas.** One database makes this simpler, but a per-app
  restore (one app corrupted, others fine) is not possible without a plan.
- **Who answers at 2am?** Unchanged from the September debate, and unsolved.
