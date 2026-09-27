import type { components } from '../../api/schema';

// Aliases over the generated schema, for the reason given in features/orders/types.ts.
export type FulfilmentOrder = components['schemas']['FulfilmentOrderResponse'];

export type FulfilmentOrderPage = components['schemas']['FulfilmentOrderPageResponse'];

export type OrderStatusChange = components['schemas']['OrderStatusResponse'];
