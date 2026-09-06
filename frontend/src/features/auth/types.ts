import type { components } from '../../api/schema';

/*
 * Aliases over the generated schema, like features/orders/types.ts. These were hand-written
 * while the backend had no auth endpoint and there was nothing to alias; now that
 * POST /api/auth/login exists, a backend rename breaks this build instead of breaking at runtime.
 */

export type Credentials = components['schemas']['LoginRequest'];

export type Registration = components['schemas']['RegisterRequest'];

export type PasswordChange = components['schemas']['ChangePasswordRequest'];

export type Session = components['schemas']['SessionResponse'];
