---
description: Record an architecture decision in docs/adr/
argument-hint: <short title>
---

Write an ADR for: `$ARGUMENTS`

1. Find the highest-numbered file in `docs/adr/` and use the next number, zero-padded to four
   digits.
2. Create `docs/adr/NNNN-<kebab-case-title>.md`:

```markdown
# NNNN. <Title>

**Date:** YYYY-MM-DD
**Status:** Accepted

## Context

What forced this decision. The constraints that were real at the time.

## Decision

What was decided, in the active voice.

## Consequences

What this makes easy, what it makes hard, and what it rules out.
Include the costs — an ADR that lists only benefits is not being honest.

## Alternatives considered

Each option, and the specific reason it lost.
```

Fill every section from the conversation. If a section cannot be filled because the
information was never discussed, ask rather than inventing a rationale.
