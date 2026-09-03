import type { Order, OrderPage } from '../features/orders/types';
import { request } from './client';

// The `| undefined` on each member is required, not noise: exactOptionalPropertyTypes is on,
// so `{ cursor }` where cursor is `string | undefined` does not satisfy a bare `cursor?: string`.
export function listOrders(params: {
  cursor?: string | undefined;
  limit?: number | undefined;
}): Promise<OrderPage> {
  const search = new URLSearchParams();
  search.set('limit', String(params.limit ?? 20));
  if (params.cursor !== undefined) {
    search.set('cursor', params.cursor);
  }

  return request<OrderPage>(`/api/orders?${search.toString()}`);
}

export function getOrder(id: string): Promise<Order> {
  return request<Order>(`/api/orders/${id}`);
}

export function placeOrder(input: { sku: string; quantity: number }): Promise<string> {
  return request<string>('/api/orders', { method: 'POST', body: JSON.stringify(input) });
}
