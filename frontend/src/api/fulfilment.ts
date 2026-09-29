import type { FulfilmentOrderPage, OrderStatusChange } from '../features/fulfilment/types';
import { request } from './client';

// The `| undefined` on each member is required, not noise: exactOptionalPropertyTypes is on,
// so `{ cursor }` where cursor is `string | undefined` does not satisfy a bare `cursor?: string`.
export function listOrdersToFulfil(params: {
  cursor?: string | undefined;
  limit?: number | undefined;
}): Promise<FulfilmentOrderPage> {
  // No status parameter: the API defaults to Placed, which is the queue this screen works.
  const search = new URLSearchParams();
  search.set('limit', String(params.limit ?? 20));
  if (params.cursor !== undefined) {
    search.set('cursor', params.cursor);
  }

  return request<FulfilmentOrderPage>(`/api/fulfilment/orders?${search.toString()}`);
}

export function shipOrder(id: string): Promise<OrderStatusChange> {
  return request<OrderStatusChange>(`/api/fulfilment/orders/${id}/ship`, { method: 'POST' });
}
