# Human-readable code standard

Required by Chris on September 25, 2026. Applies to implementation, tests, SQL, scripts,
and configuration, including AI-generated work. Humans must be able to understand,
debug, and safely change this codebase without reconstructing the conversation that
produced it. Readability and maintainability are acceptance criteria, not optional polish.

## Simple by default

- Prefer explicit, familiar control flow and straightforward data transformations.
  Avoid clever expressions, dense one-liners, hidden side effects, and unnecessary nesting.
- Use names that describe domain meaning and intent. Avoid unexplained abbreviations,
  generic helper names, and booleans whose meaning is unclear at the call site.
- Keep functions and types focused on a coherent responsibility. Split code when it makes
  the behavior easier to follow; do not scatter a simple operation across many tiny layers.
- Introduce an abstraction only for an existing need with a concrete explanation. Avoid
  speculative frameworks, configuration-driven behavior, and generic machinery for a
  single straightforward operation. Preserve established architectural boundaries.
- Prefer some obvious local code over an abstraction that obscures meaning. Centralize
  shared business/security rules when duplication could cause inconsistent behavior.
- Make dependencies, state changes, transaction boundaries, authorization decisions, and
  failure handling easy to locate. Expected failures should have explicit outcomes.
- Use idiomatic language and framework features when they help comprehension. Do not
  replace standard mechanisms with custom implementations merely to look simpler.
- Optimize for a human maintainer's understanding, not minimum line count. Accept extra
  lines when they clarify behavior. Complexity needed for correctness, security, or measured
  performance must be explained and tested, not removed for cosmetic simplicity.

## Strong, useful comments

Comments are expected where they materially help a human understand or safely change code.
Self-explanatory names do not replace explanations of non-obvious constraints.

- Explain **why** the implementation exists and what must remain true: business rules,
  security boundaries, ordering requirements, concurrency, retries, compatibility, and
  surprising framework behavior.
- For complex operations, give a short overview of the flow and explain the difficult
  steps beside the code. If the explanation is harder than necessary, simplify the code
  first rather than adding an essay to defend it.
- Document shared/public contracts where callers need to know inputs, outputs, side
  effects, ownership/lifetimes, or failure semantics. Do not mechanically document every
  private helper or repeat its signature in prose.
- Make comments factual and proportional. Avoid repeating what an obvious statement does,
  long repeated architecture explanations, praise of the implementation, or unsupported
  claims such as “cannot fail” or “guaranteed secure.”
- Keep essential safety explanations local. Link to a design document for extended
  rationale, but do not require a document hunt to understand a dangerous operation.
- Update or remove comments when behavior changes. Stale comments are defects.
- Explain unusual test setup and the failure scenario a regression protects. Tests should
  read as understandable examples, not puzzles or mirrors of implementation details.

## Review and acceptance

Every substantive implementation review must assess human readability alongside correctness
and security. Passing builds and tests alone does not satisfy this standard.

The reviewer should be able to answer:

1. Can a maintainer unfamiliar with this change trace the successful path and failure paths?
2. Are names, dependencies, state changes, and important boundaries clear?
3. Is every added layer or abstraction justified by an actual need?
4. Do comments explain the non-obvious reasons and constraints accurately?
5. Could the same behavior be expressed more simply without weakening correctness,
   security, or required performance?
6. Do the tests explain the behavior and make a future safe change easier?

Require revision when unnecessary complexity, misleading comments, or obscured behavior
materially impedes understanding or safe modification. Give a concrete example and a
simpler alternative; do not turn personal formatting preferences into blockers.

Apply this standard to new and touched code. Address nearby complexity when necessary to
make the change understandable, but do not launch unrelated repository-wide rewrites.
Generated EF migrations and similar tool-owned output should be regenerated through the
normal workflow; inspect the resulting SQL and explain non-obvious custom migration steps
rather than hand-editing generated snapshots for style.

## Vertical Slice Architecture is required

Organize application behavior by use case, not by technical layer. A maintainer working
on “assign ticket” should find the request, handler, and endpoint together in
`Features/<Area>/AssignTicketFeature.cs` and follow the workflow in a small, predictable
set of files.

- Keep feature-specific validation, orchestration, and outcomes in the slice. Use nested
  command/query handlers and endpoints. Do not create empty request DTOs for operations
  that have no request body solely to satisfy a template.
- Endpoints translate HTTP and invoke the handler. Handlers express the use case; they
  must not merely forward into a chain of workflow services that hides the behavior.
- Keep persistence and external I/O behind focused interfaces where appropriate. A
  service should describe a useful capability, not move an entire feature elsewhere.
- Extract shared business invariants when duplication risks divergence. Keep tenancy,
  authentication, authorization, audit, and other established cross-cutting mechanisms
  centralized. Do not copy security checks into each handler in the name of locality.
- Do not invoke another feature's handler or depend on its private implementation.
  Extract the genuinely shared rule/capability, or use the owning service's published
  contract across service boundaries.
- Prefer readable local control flow over generic pipelines, base handlers, service
  locators, or extra indirection. Existing endpoint discovery is an intentional
  infrastructure exception, not permission to obscure business workflows.

### Enforcement

`boundary.tests/VerticalSliceTests.cs` runs in CI for every registered API and requires
concrete endpoints to be nested in a static named feature, with a local command/query
handler and the expected feature file. This proves structure, not semantic locality or
that every declaration is physically in that file.

Every substantive review must additionally trace an affected use case and check that its
validation, decisions, and state changes remain understandable within the slice; shared
extractions must have a concrete reason. Unnecessary forwarding layers, cross-feature
implementation dependencies, or scattered feature logic are blocking maintainability
findings when they impede safe changes. Report the concrete path and simpler alternative.

Do not add generic allowlists or disable checks to bypass a failure. A necessary exception
must document its specific scope and rationale and receive review; an exception does not
relax the rule for other features. Structural compliance alone never establishes readable VSA.
