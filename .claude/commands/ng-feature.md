---
description: Scaffold an Angular feature - routed standalone component, service, model, specs
argument-hint: <feature-name>
---

Create the Angular feature `$ARGUMENTS` under `frontend/src/app/`.

Read `frontend/CLAUDE.md` first and follow it exactly.

## Files

```
frontend/src/app/<feature-name>/
  <feature-name>.component.ts        standalone, OnPush, inject(), signals
  <feature-name>.component.html      @if / @for with track
  <feature-name>.component.scss
  <feature-name>.component.spec.ts
  <feature-name>.service.ts          typed HttpClient calls, typed error handling
  <feature-name>.service.spec.ts
  <feature-name>.model.ts            interfaces matching the Api DTOs
```

## Requirements

- Standalone component, `ChangeDetectionStrategy.OnPush`, dependencies via `inject()`
- State in signals; derived state in `computed()`
- Every subscription uses `takeUntilDestroyed()`, or the template uses the `async` pipe
- `catchError` returns a typed failure or rethrows — it never returns `of(null)`
- Register the route in the app routes with `loadComponent` for lazy loading
- No `any`, no `!`

## Finish

Run `npm run lint --prefix frontend`, then dispatch the `angular-reviewer` agent over the diff.

If Node is not installed, say so plainly and skip the lint step — do not report it as passing.
