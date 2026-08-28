---
name: angular-conventions
description: Use when writing or modifying Angular or TypeScript in this repo - standalone components, signals, OnPush, control flow, subscription lifecycle, and error handling.
---

# Angular Conventions

Lint runs with `--max-warnings 0`: one warning fails the build.

## Components

Standalone only. `OnPush` always. Dependencies via `inject()`.

```typescript
@Component({
  selector: 'app-orders',
  templateUrl: './orders.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OrdersComponent {
  private readonly service = inject(OrdersService);

  readonly orders = signal<readonly Order[]>([]);
  readonly openCount = computed(() => this.orders().filter((o) => o.isOpen).length);
}
```

- State in `signal()`, derived state in `computed()`. Never recompute in the template.
- Signals hold immutable values. Replace the array; do not mutate it — `OnPush` will not see
  a mutation.

## Templates

`@if` / `@for` / `@switch`. `@for` requires `track`.

```html
@if (orders().length > 0) {
  @for (order of orders(); track order.id) {
    <app-order-row [order]="order" />
  }
} @else {
  <p>No orders.</p>
}
```

## Subscriptions

Prefer the `async` pipe. Where you must subscribe, use `takeUntilDestroyed()`. Manual
`Subscription` fields and `ngOnDestroy` bookkeeping are a leak waiting to happen.

```typescript
this.service.watch()
  .pipe(takeUntilDestroyed())
  .subscribe((value) => this.orders.set(value));
```

## Error handling

Never swallow a failure.

```typescript
// Wrong - the user sees nothing and the bug is invisible
catchError(() => of(null))

// Right - surface it, typed
catchError((error: HttpErrorResponse) => {
  this.error.set(toUserMessage(error));
  return EMPTY;
})
```

A global `ErrorHandler` and a typed HTTP interceptor cover what components do not.

## Types

- No `any`. No `!`.
- Typed reactive forms — never `FormGroup<any>`.
- Explicit return types on functions.
- Models mirror the Api DTOs; keep them in `<feature>.model.ts`.
