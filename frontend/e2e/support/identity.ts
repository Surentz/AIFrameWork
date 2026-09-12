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

/** PasswordPolicy.MinimumLength is 12; this is comfortably above it. */
export const PASSWORD = 'a long enough e2e password';
