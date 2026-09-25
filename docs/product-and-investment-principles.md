# Product and investment principles

Agreed with Chris on September 25, 2026. This records the current direction for product
planning and AI-assisted development; it is not evidence of customer demand.

## Founding thesis

Recorded September 25, 2026, in Chris's words and intent.

Chris's employer recently spent seven figures on SAP. He considers that close to indefensible:
with current AI capability, he sees no reason that should be a rational purchase for most
companies. He accepts that trust and marketing are large parts of why it still is, and intends
to use AI assistance to help close that gap. He is one person with no prospective customers
today. The eventual goal is a viable alternative to "SAP lite".

**What the thesis gets right.** Most of a seven-figure ERP programme is not licence cost. The
larger share is implementation: mapping the customer's processes onto the vendor's model, data
migration, integration, and change management. A meaningful part of that work exists *because of
the software's complexity*, not because of the customer's problem. That is real, and it is the
part worth attacking.

**Where it needs care.** AI collapses the cost of *building* ERP features. It does not collapse
the cost of *implementing* ERP inside a company — deciding how this business actually runs,
extracting data nobody documented, and getting people to change what they do. The money mostly
went to the second thing. A cheaper way to write the software does not automatically address it.

**On feature parity.** Parity with SAP is a fool's errand, but not because of code volume. It is
because SAP's surface is the *union* of thousands of customers' requirements: every customer uses
a small fraction, and each uses a different fraction. Chasing the union means shipping paths no
one has tested, in a domain where being wrong about money is worse than having no system.

The useful distinction:

- **The stable core of ERP** — double-entry, AP/AR, inventory and valuation, order-to-cash,
  procure-to-pay — is well understood, documented, and has not moved in decades. A very small
  team with AI assistance can genuinely build it.
- **The long tail** — industry-specific, jurisdiction-specific, regulation-specific behaviour —
  is where the incumbent's moat and the consulting revenue actually live. That tail is the fool's
  errand.

So the goal is coverage of the stable core, shaped for a specific kind of business, not parity.
"Shaped for" is the product: the configuration a competitor charges six figures to perform.

**On trust.** The gap is larger than cost alone explains, and it is not irrational. Nobody buys
the system that runs their books from one person with no customers, no references, no audit
history, and no answer to what happens if he stops. Scope is the main lever: "this one workflow"
is a far smaller bet than "our ERP". Cheap exit — data portability, open formats, documented
restore — makes arriving cheap. Operational evidence beats assurances, which is why the recovery
drill and the isolation proofs are commercial artifacts, not just engineering ones.

**On human-readable code.** Required, and the reason is commercial rather than aesthetic. For a
solo founder the binding constraint is not writing features, it is understanding what was written
six months ago. For a prospective customer, "one person generated this with AI" is a risk;
"a competent developer could take this over" is the mitigation. Maintainability is part of the
trust story.

## Starting position

There is no identified first customer or validated commercial opportunity yet. The platform
is a technical and commercial hypothesis. Tickets is a proving application; its presence
in the repository does not establish it as the product we should sell. ERP remains a
possible direction, not a validated commitment.

Updated September 25, 2026: **AI credits are a constrained budget.** This supersedes the
earlier instruction to spend aggressively because unused credits expire. Optimize useful
progress and evidence per credit. Expiry does not justify unnecessary work. Calculated
product experiments remain welcome; keep their scope bounded and lasting costs controlled.

## Investment posture

Protect the downside and pursue substantial upside. We are willing to build ahead of
validated demand and take calculated product risks. Building a working example can itself
be a validation experiment; interviews or buyer commitments are not prerequisites for
every prototype.

- Favor inexpensive infrastructure, reversible changes, and isolated experiments.
- Scrutinize recurring cash costs, support obligations, integrations, and architectural
  complexity more closely than AI-assisted development effort.
- Use experiments to discover valuable customer problems and repeatable workflows.
- Spend credits to produce learning or useful capability, not activity or code volume.
- Further investment should reflect new evidence and prospective value, not sunk effort.
- Do not impose a new spending limit or treat this agreement as approval for purchases,
  deployments, external outreach, or live-data changes. Existing authorization rules stand.

## A lightweight record for each meaningful bet

Record the following in the existing backlog or the relevant experiment document. Keep it
proportionate; a small prototype does not need a business plan.

1. **Hypothesis:** who might buy, what costly or frequent problem they have, and why our
   approach could improve on their current alternative.
2. **Experiment:** the smallest useful build or investigation that tests the hypothesis.
3. **Downside:** cash, human time, support, maintenance, and data exposure it introduces;
   how we can stop or discard it.
4. **Evidence sought:** what result would justify expanding, changing, or shelving it.
5. **Result and next decision:** observations, uncertainties, and the next bounded step.

Label evidence accurately: a hypothesis, a working demo, expressed interest, a pilot, and
willingness to pay are different things. None automatically establishes the next.

## Quality and security are separate from product risk

Be bold about what we try and rigorous about what we claim works. Experimental commercial
scope does not lower the security or data-integrity bar for real customer use.

- Use synthetic data for unproven workflows; real customer data requires the documented
  readiness gates, including isolation, access control, recovery, and migration safety.
- Verify behavior where it can fail: real PostgreSQL and runtime grants, actual HTTP auth
  and CSRF, and browser journeys where applicable.
- Keep implemented, verified, incomplete, and deferred behavior distinct in the
  [quality ledger](backlog.md). Test counts do not establish product readiness.
- Use the risk-based review process below and resolve blocking findings.
- Prefer a complete narrow workflow over expanding infrastructure without an exercised use.

## Credit-conscious development and review

- Start each cycle with one bounded objective and observable acceptance criteria. Finish
  and verify it before adding unrelated features. Bound speculative experiments by a
  deliverable and a stop/reassess condition before starting.
- Read the quality ledger and relevant diff first. Inspect affected code and dependencies;
  do not repeatedly rediscover the entire repository or restart broad QC without cause.
- Use deterministic builds, tests, and architecture checks for repeatable verification.
  Run focused tests during iteration, then the required full suite at acceptance. Repeat
  checks only after relevant changes, failures, or unresolved concerns warrant it.
- Every substantive change gets a correctness and readability review. For routine,
  reversible changes, a focused self-review plus relevant checks is sufficient; label it
  accurately rather than claiming independent review.
- **Require independent review for changes affecting security, tenant isolation, identity,
  authorization, database grants, data integrity, migrations, concurrency, or major
  architecture.** Use a separate reviewer when available and give it the objective, diff,
  affected contracts, and test evidence. Do not spawn parallel reviews for routine work.
  If independent review is unavailable, complete independent work and record that review
  as pending; do not waive the requirement or claim readiness for the affected change.
- Re-review fixes and affected paths, not the whole project by default. After two failed
  repair rounds on the same issue, diagnose the underlying assumption before patching
  again. Do not broaden the cycle to absorb unrelated suggestions.
- Record a short handoff in the existing quality ledger: objective, changed behavior,
  checks/results, review status, unresolved risks, and next step. Avoid redundant reports.
- Required security and acceptance checks are not optional budget cuts. If resources are
  insufficient, reduce scope and state what remains unverified instead of lowering the bar.
- No numeric credit limit or model preference has been agreed. Do not invent one or ask
  the human to approve routine steps; surface a concrete resource blocker when necessary.

## Human maintainability is part of value

Human developers may need to operate and extend this codebase. Chris requires the simplest
practical implementation with strong, accurate comments explaining non-obvious behavior.
Follow the [human-readable code standard](human-readable-code.md) in every implementation
and substantive review. Spending AI credits must not create code whose future maintenance
costs outweigh the capability it provides.

## Assistant responsibility and human involvement

Chris expects the assistant to challenge engineering quality, security, and commercial
value where appropriate—not merely implement an expanding feature list. Research customer
problems and alternatives, assess delivery/support costs, propose experiments, and report
what the evidence does and does not support. Do not promise demand or security beyond it.

Minimize human coordination within authorized work. Escalate material product choices,
missing access, and consequential actions outside existing authorization. This agreement
does not grant new commit or deployment permissions.

## Still unresolved

- Which prospective customer groups Chris can reach for candid feedback.
- The first commercial hypothesis and intended buyer.
- The data sensitivity and deployment constraints of an eventual pilot.
- Human-time and recurring-cash budgets. Previously recorded time limits need explicit
  reconciliation when planning the next investment; expiring credits alone do not revoke
  them or authorize a larger financial commitment.
