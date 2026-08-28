# 0001. Record architecture decisions

**Date:** 2026-08-27
**Status:** Accepted

## Context

Architectural choices made early in a project get forgotten, and are then either re-litigated
or violated by people who never knew the reasoning. A greenfield repo is the cheapest possible
moment to start recording them.

## Decision

Significant architectural decisions are recorded as ADRs in `docs/adr/`, numbered sequentially
and never rewritten once accepted. A decision that is later reversed gets a new ADR that
supersedes the old one; the old one stays in place with its status updated.

Use `/adr <title>` to create one.

## Consequences

Anyone reading the repo can reconstruct why it looks the way it does, including the options
that lost. The cost is the discipline to write one at the time rather than afterwards, and
ADRs written late are usually rationalisations rather than records.

## Alternatives considered

**A wiki or Confluence page.** Drifts from the code and needs separate access. ADRs are
reviewed in the same pull request as the change they describe.

**Nothing.** The default. It works until the second developer arrives, or until the first one
returns after six months.
