---
name: angular-testing
description: Use when writing or modifying Angular tests in this repo - Vitest, component testing, HttpClient mocking, and what not to test.
---

# Angular Testing

Runner: Vitest. Karma is deprecated and is not used here.

## Components

Test behaviour through the rendered output, not through the class instance.

```typescript
describe('OrdersComponent', () => {
  it('renders one row per order', async () => {
    await TestBed.configureTestingModule({
      imports: [OrdersComponent],
      providers: [
        provideHttpClientTesting(),
        { provide: OrdersService, useValue: { watch: () => of([anOrder(), anOrder()]) } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(OrdersComponent);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelectorAll('app-order-row')).toHaveLength(2);
  });
});
```

## Services

`provideHttpClientTesting()` with `HttpTestingController`. Assert the request method, URL, and
body, then flush a response. Always call `httpTesting.verify()` in `afterEach`.

## Rules

- Test what the user experiences: rendered output, emitted events, requests made.
- Do not test framework behaviour — that `@Input` assignment works is Angular's test, not yours.
- Do not assert on private fields or call private methods.
- One behaviour per `it`. The name completes the sentence "it ...".
- Prefer a fake object over a mocking framework for a service you own.
- No arbitrary timeouts. Await the fixture's stability instead.

## What not to test

Getters that only return a field. `computed()` that only reads one signal. Template bindings
with no logic. These cost maintenance and catch nothing.
