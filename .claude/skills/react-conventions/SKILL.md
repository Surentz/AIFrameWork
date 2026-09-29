---
name: react-conventions
description: Use when writing or modifying React or TypeScript in this repo - function components, hooks, TanStack Query, forms, and error handling.
---

# React Conventions

Lint runs with `--max-warnings 0`: one warning fails the build.

## Components

Function components only. No classes. Props typed with an explicit interface, never inline `any`.

```tsx
interface OrderRowProps {
  readonly order: Order;
}

export function OrderRow({ order }: OrderRowProps): JSX.Element {
  return <tr><td>{order.sku}</td><td>{order.quantity}</td></tr>;
}
```

- Derived values are computed during render, not stored in state. `useMemo` only when a
  measurement says it matters.
- State is immutable. Replace the array; do not mutate it.
- No `useEffect` for data fetching — that is TanStack Query's job.

## Server state

TanStack Query owns everything that comes from the API. Component state owns only what the
user is currently typing.

```tsx
const { data, isPending, error } = useQuery({
  queryKey: orderKeys.list(cursor),
  queryFn: () => listOrders({ cursor }),
});
```

- Query keys live in one exported `orderKeys` object per feature. Never inline a key literal.
- A mutation that changes a list invalidates that list's key in `onSuccess`.

## Error handling

Never swallow a failure.

```tsx
// Wrong - the user sees nothing and the bug is invisible
try { await placeOrder(input); } catch { /* ignore */ }

// Right - surface it, typed
const mutation = useMutation({ mutationFn: placeOrder });
if (mutation.error) { return <ErrorPanel error={mutation.error} />; }
```

Every query and mutation renders its `error` state. A component with a fetch and no error
branch is incomplete.

## Types

- No `any`. No `!`.
- Explicit return types on exported functions.
- Models mirror the Api DTOs; keep them in `<feature>/types.ts`.

## Accessibility

`jsx-a11y` is weaker than the template linting this repo used to have — it sees JSX only, not
runtime composition. Label every input, give every interactive element an accessible name,
and do not rely on the linter to catch what it cannot see.
