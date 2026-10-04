import type { components } from '../../api/schema';

// Aliases over the generated schema rather than hand-written interfaces. Components and tests
// keep importing `Order` and `OrderPage` from here, so nothing else churns — but the alias stops
// compiling the moment the backend renames or drops a property, which is the entire point.
// Before this, types.ts restated OrderDtos.cs from memory and nothing connected the two: a
// renamed property left the app compiling, the tests green, and only production broken.
//
// Regenerate with `npm run generate:api` after any backend contract change. Do not hand-edit
// src/api/schema.d.ts.
export type Order = components['schemas']['OrderResponse'];

export type OrderListItem = components['schemas']['OrderListItemResponse'];

export type OrderPage = components['schemas']['OrderPageResponse'];

// An export of the caller's own orders (ADR 0029). Status is 'Requested' | 'Ready' | 'Failed';
// Failed is never stored, the API derives it from a request the build job gave up on.
export type OrderExport = components['schemas']['OrderExportResponse'];

export type OrderExportState = components['schemas']['OrderExportState'];
