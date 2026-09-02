# 0004. React over Angular

**Date:** 2026-09-03
**Status:** Accepted

## Context

The repository was scaffolded with Angular tooling that was never exercised. The `frontend/`
directory held exactly two files (`CLAUDE.md` and `eslint.config.js`), and the Angular
workspace was never created. The founding design spec (`docs/superpowers/specs/2026-08-27-claude-framework-design.md`)
listed "scaffold the solution and Angular workspace" in its deferred section. The .NET half
was completed, but Angular remained unstarted.

At the time this choice was made, Angular tooling was selected because it pairs well with
Clean Architecture's layering on the backend: a server-side framework offering structured
conventions and strong accessibility checking. However, the scope and complexity of Angular
when used as a client-side SPA only began to feel expensive against the work it would add
for a first UI slice, and the ecosystem's pace of change (recent migrations from tslint to
ESLint, the standalone API, evolution of routing and state management) meant the gap between
this repo's age and current Angular practice was already observable. Starting the Angular
workspace today would mean starting from that moving target, not from a stable design point.

The choice to reconsider became concrete when the first feature planning arrived: a UI is
needed to exercise the backend's order API, and the decision on the frontend stack cannot be
deferred further.

## Decision

Replace Angular with **Vite + React + TypeScript** as the client-side SPA. This choice gives
us the stack that reduces friction for the first slice without sacrificing what the .NET
backend already provides — a stable, layered API to call.

- **Vite** as the build tool and dev server — fast rebuilds, minimal config, ESM-native, and
  widely used with React.
- **React** as the component framework — simpler mental model than Angular's directives and
  decorators, lighter abstraction over the DOM, and a smaller ecosystem to commit to for a
  first UI.
- **React Router** for client-side routing — the standard choice, proven, and sufficient for
  a SPA of this scale.
- **TanStack Query** (React Query) for server state management — it separates concerns clearly
  (cache invalidation, refetch strategies, optimistic updates) and keeps the component tree
  from becoming the state tree.
- **TypeScript** — no change from the Angular plan, and our backend is C#, so types are
  already the norm.

## Consequences

### Cost

The switch introduced one real reduction in validation capability: **JSX accessibility
analysis is weaker than Angular's template-based checks.** The `jsx-a11y` ESLint rule sees
JSX source code only; it cannot see runtime composition (conditionals that render different
elements, dynamic attribute binding) or component APIs that declare what a component requires.
Angular's `@angular-eslint/templateAccessibility` works on the final template the browser sees,
with full component metadata available. On the frontend, we lose some static guarantees and
must compensate with runtime testing and code review. This is a real cost, not a minor footnote.

### Benefit

The cost of this choice was predominantly configuration and documentation, not application
code. The `frontend/` directory held no code to rewrite — only two infrastructure files. What
existed was the Angular decision in the plan itself and in the design spec's deferred list, and
the Angular-focused skills (`/ng-feature`, toolchain configurations, ESLint and TypeScript
rules). All of that was already changed during Task 1, before the workspace existed. This is
why the switch was possible to make with such low friction: Angular was planned but never built.

### Historical record

The founding design spec (`docs/superpowers/specs/2026-08-27-claude-framework-design.md`)
remains unedited. That file is a dated historical record of decisions made at scaffold time.
This ADR is the forward record, recording the correction that happened later. This precedent
is set by ADR 0003, which also documents a correction to its own original claims after a
later plan altered what was built. The design spec's integrity as a historical snapshot
is more valuable than removing the record that Angular was chosen, then reconsidered.

## Alternatives considered

**Next.js.** Rejected because its server half (API routes, server components, server actions)
overlaps the .NET API. This would force a split: some requests go to the Vite + React
frontend's Next.js server, others to the backend. Auth would need to be duplicated or
carefully split; data fetching would need to be replicated as both server-side (in Next.js) and
client-side (for interactive queries). The gain over a pure SPA — SSR, zero-JS pages — does
not pay back the cost of maintaining two servers when one stable API already exists.

**TanStack Router.** Rejected in favor of React Router. TanStack Router's type-safe route
parameters and search params are superior, and its FileRoute-based organization is elegant.
But React Router is the familiar baseline, and the unfamiliarity of TanStack Router does not
justify its learning curve for a first UI slice. React Router is sufficient; TanStack Router
can be reconsidered when routing becomes a bottleneck or when the team is more settled on React.

**Remix.** Not considered; it sits between Next.js and pure SPA in terms of overlap with the
backend and would introduce the same auth/data-fetching split concerns as Next.js.

**Solid.js, Svelte, Vue.** Not considered. React's ecosystem size and community maturity are
unmatched in this space, and the choice needed to be made and built now. Alternatives with
smaller ecosystems were rejected on the principle that the cost of community and hiring
knowledge outweighs syntactic advantages for a team starting a codebase.
