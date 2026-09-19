import type { components } from '../../api/schema';

// Aliases over the generated schema, exactly like features/orders/types.ts — a backend rename
// breaks the build here instead of at runtime. Regenerate with `npm run generate:api`; never
// hand-edit src/api/schema.d.ts.
export type Notification = components['schemas']['NotificationResponse'];

export type NotificationPage = components['schemas']['NotificationPageResponse'];

export type NotificationReadResult = components['schemas']['NotificationReadResponse'];

export type UnreadCount = components['schemas']['UnreadCountResponse'];

// A string union ('OrderPlaced' | 'OrderShipped' | ...), not a number, because the API
// serializes enums by name. Switching over it is a compile error when the backend adds a kind,
// which is the whole reason the contract carries names rather than integers.
export type NotificationKind = components['schemas']['NotificationKind'];
