# 0002. Clean Architecture with a machine-enforced dependency rule

**Date:** 2026-08-27
**Status:** Accepted

## Context

The backend needed a house style that both people and coding agents could follow. Clean
Architecture's boundaries are well understood, but a single feature spans four projects, so
edits fan out and the dependency rule is easy to break one `using` at a time. Documented
conventions are honoured only as long as they are remembered.

## Decision

Clean Architecture, with `Domain`, `Application`, `Infrastructure`, and `Api`.

The dependency rule is enforced by a PreToolUse hook that blocks the edit
(`.claude/hooks/dependency-rule.ps1`), not by documentation. Per-layer `CLAUDE.md` files put
each layer's rules in front of an agent at the moment it opens that directory. Warnings are
errors from all three sources — compiler, analyzers, and build — so nullability and exception
antipatterns fail the build rather than accumulating.

## Consequences

The rule cannot rot: a violating edit is rejected with an explanation rather than reviewed
later. Warnings cannot accumulate, because there is no warning state to accumulate in.

The costs are real. Four projects per feature is more ceremony than a layered app needs at
small scale. Warnings-as-errors means an SDK or analyzer upgrade can break the build on code
nobody touched. And one row of the matrix — `Api` → `Infrastructure`, legal for DI
registration only — cannot be checked by a hook, because nothing distinguishes a
`services.AddScoped<>()` call from a controller reaching into a repository. That row is
carried by review.

## Alternatives considered

**Vertical Slice with Minimal APIs.** A tighter blob of context per feature and fewer files
per change, which suits agent work better. Lost because the team wanted the more familiar and
widely documented structure.

**Layered controllers/services.** Lowest ceremony, but the least structural guidance and the
fastest to degrade as the app grows.

**Documenting the dependency rule without enforcing it.** Rejected: this is precisely the rule
that gets broken silently, one `using` at a time.
