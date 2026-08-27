# Frontend

Angular workspace.

## After `ng new`, apply this `tsconfig.json` delta

`ng new` generates this file, so these cannot be pre-written — apply them once:

```jsonc
{
  "compilerOptions": {
    "strict": true,
    "noUncheckedIndexedAccess": true,
    "exactOptionalPropertyTypes": true,
    "noImplicitOverride": true,
    "noFallthroughCasesInSwitch": true,
    "noImplicitReturns": true
  },
  "angularCompilerOptions": {
    "strictTemplates": true,
    "strictInjectionParameters": true
  }
}
```

## Conventions

- **Standalone components only.** No `NgModule`.
- **`inject()`** over constructor injection.
- **Signals** for component state; `computed()` for derived state.
- **`ChangeDetectionStrategy.OnPush`** on every component.
- **`@if` / `@for` / `@switch`**, not `*ngIf` / `*ngFor`. `@for` needs `track`.
- **`takeUntilDestroyed()`** for subscription lifecycle. No manual `Subscription` fields,
  no `ngOnDestroy` bookkeeping.
- **Typed reactive forms.** Never `FormGroup<any>`.
- **Never swallow an error.** `catchError` must rethrow, return a typed failure, or
  surface the problem to the user — never `of(null)` to make a red line go away.

## Commands

| | |
|---|---|
| `npm start` | dev server |
| `npm run build` | production build |
| `npm test` | Vitest |
| `npm run lint` | `ng lint --max-warnings 0` |

Lint runs with `--max-warnings 0`: one warning is a failure.
