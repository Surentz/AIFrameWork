---
description: Scaffold a React feature - route, components, typed API calls, query hooks, tests
argument-hint: <feature-name>
---

Create the React feature `$ARGUMENTS` under `frontend/src/features/`.

Read `frontend/CLAUDE.md` first and follow it exactly.

## Files

```
frontend/src/features/<feature-name>/
  <FeatureName>List.tsx          list view
  <FeatureName>List.test.tsx
  <FeatureName>Detail.tsx        detail view
  <FeatureName>Detail.test.tsx
  queries.ts                     query keys + useQuery/useMutation hooks
  types.ts                       interfaces matching the Api DTOs
frontend/src/api/<feature-name>.ts    typed fetch calls
frontend/src/test/handlers.ts         MSW handlers for the new endpoints
```

## Requirements

- Function components with explicit prop interfaces and explicit return types
- Server state through TanStack Query; query keys in one exported `<feature>Keys` object
- Every query and mutation renders its error state
- A mutation that changes a list invalidates that list's key in `onSuccess`
- Register the route in `frontend/src/routes.tsx`
- No `any`, no `!`
- MSW handlers added for every new endpoint, because `onUnhandledRequest: 'error'` will
  otherwise fail the test rather than silently pass it

## Finish

Run `npm run lint --prefix frontend`, then dispatch the `react-reviewer` agent over the diff.

If Node is not installed, say so plainly and skip the lint step — do not report it as passing.
