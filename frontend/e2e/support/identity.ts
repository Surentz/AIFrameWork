import { randomUUID } from 'node:crypto';

/**
 * A username no other test will pick. `Date.now()` is not enough once workers run in parallel:
 * two of them registering inside the same millisecond collide, and the 409 surfaces as an
 * unrelated navigation timeout.
 *
 * RegisterUserValidator allows only [A-Za-z0-9._-] and User.MaxUsernameLength is 32, so the
 * hyphen is fine and the 16-character result is comfortably inside the limit.
 */
export function uniqueUsername(prefix = 'e2e'): string {
  return `${prefix}-${randomUUID().replaceAll('-', '').slice(0, 12)}`;
}

/** The same idea for order SKUs, which tests assert on by exact text. */
export function uniqueSku(): string {
  return `SKU-E2E-${randomUUID().replaceAll('-', '').slice(0, 8).toUpperCase()}`;
}

/**
 * The same idea for catalogue SKUs. Separate from `uniqueSku` so a catalogue row and an order
 * line are never confused in an assertion — and necessary rather than cosmetic: the products
 * table has a UNIQUE index on Sku, so two tests picking one literal would collide on a 409.
 */
export function uniqueProductSku(): string {
  return `CAT-E2E-${randomUUID().replaceAll('-', '').slice(0, 8).toUpperCase()}`;
}

/** PasswordPolicy.MinimumLength is 12; this is comfortably above it. */
export const PASSWORD = 'a long enough e2e password';
