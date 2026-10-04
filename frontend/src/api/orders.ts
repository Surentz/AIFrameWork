import type { Order, OrderExport, OrderPage } from '../features/orders/types';
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

// Exports of the caller's own orders (ADR 0029). Answers 202 with the export it started, or with the
// one already being built - a second request while one is in progress starts nothing new.
export function requestOrderExport(): Promise<OrderExport> {
  return request<OrderExport>('/api/orders/exports', { method: 'POST' });
}

export function listOrderExports(): Promise<OrderExport[]> {
  return request<OrderExport[]>('/api/orders/exports');
}

// A plain URL for an <a href>, not a fetch: the session cookie is same-origin and the API sends
// Content-Disposition: attachment, so the browser saves the file itself with no blob handling here.
export function orderExportDownloadUrl(id: string): string {
  return `/api/orders/exports/${id}/download`;
}
