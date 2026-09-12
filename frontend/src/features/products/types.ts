import type { components } from '../../api/schema';

// Aliases over the generated schema rather than hand-written interfaces, exactly as
// features/orders/types.ts does — the alias stops compiling the moment the backend renames or
// drops a property, which is the entire point.
//
// Regenerate with `npm run generate:api` after any backend contract change. Do not hand-edit
// src/api/schema.d.ts.
export type Product = components['schemas']['ProductResponse'];

export type ProductListItem = components['schemas']['ProductListItemResponse'];

export type ProductPage = components['schemas']['ProductPageResponse'];

/**
 * `price` is typed `number | string` by the generator and that is correct, not untidy: ASP.NET
 * Core's web JSON defaults set AllowReadingFromString, so the API genuinely accepts both and the
 * document says so. Formatting therefore has to accept both too — see frontend/CLAUDE.md.
 *
 * No currency symbol: the API stores an amount and no currency, so rendering one would be
 * inventing information the backend never sent.
 */
export function formatPrice(price: number | string): string {
  const value = typeof price === 'number' ? price : Number(price);

  return Number.isFinite(value)
    ? value.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })
    : String(price);
}
