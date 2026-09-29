---
name: react-testing
description: Use when writing or modifying React tests in this repo - Vitest, React Testing Library, MSW, and what not to test.
---

# React Testing

Runner: Vitest. Component queries: React Testing Library. HTTP: MSW.

## MSW is not optional

MSW runs with `onUnhandledRequest: 'error'`. Without it a component that requests the wrong
URL falls through unmocked and the test still passes — the request never had to be right.

```ts
// src/test/setup.ts
beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());
```

Never `vi.mock()` the API client module. That seam sits above `fetch`, so a wrong URL, a
wrong verb, and a serialization bug all pass.

## Components

Test what the user experiences: rendered output, and the requests that went out.

```tsx
it('renders one row per order', async () => {
  render(<OrderList />, { wrapper: withQueryClient() });

  expect(await screen.findAllByRole('row')).toHaveLength(3);
});
```

- Query by role and accessible name. `getByTestId` is a last resort.
- `findBy*` to await async state. Never a fixed timeout.
- Assert the error branch renders, not just the happy path.

## Rules

- One behaviour per `it`. The name completes the sentence "it ...".
- Do not test framework behaviour — that props pass through is React's test, not yours.
- Do not assert on internal state or call internals.
- Give each test its own `QueryClient` with retries off, so a failure is not retried into a
  timeout.

## What not to test

Pure presentational components with no logic. A `useMemo` that reads one value. Prop
pass-through. These cost maintenance and catch nothing.
