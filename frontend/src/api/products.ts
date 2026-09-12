import type { Product, ProductPage } from '../features/products/types';
import { request, requestVoid } from './client';

// The `| undefined` on each member is required, not noise: exactOptionalPropertyTypes is on,
// so `{ cursor }` where cursor is `string | undefined` does not satisfy a bare `cursor?: string`.
export function listProducts(params: {
  cursor?: string | undefined;
  limit?: number | undefined;
}): Promise<ProductPage> {
  const search = new URLSearchParams();
  search.set('limit', String(params.limit ?? 20));
  if (params.cursor !== undefined) {
    search.set('cursor', params.cursor);
  }

  return request<ProductPage>(`/api/products?${search.toString()}`);
}

export function getProduct(id: string): Promise<Product> {
  return request<Product>(`/api/products/${id}`);
}

export interface CreateProductInput {
  readonly sku: string;
  readonly name: string;
  readonly description: string | null;
  // A string, not a number: the API accepts either (AllowReadingFromString), and passing what
  // the user typed through unchanged keeps a value like "1.005" intact so the server's own
  // decimal-places rule is what rejects it, rather than a float round-trip quietly changing it.
  readonly price: string;
}

export function createProduct(input: CreateProductInput): Promise<string> {
  return request<string>('/api/products', { method: 'POST', body: JSON.stringify(input) });
}

export interface UpdateProductInput {
  readonly id: string;
  readonly name: string;
  readonly description: string | null;
  readonly price: string;
}

/**
 * `requestVoid`, because this endpoint answers 204 — `request` would throw parsing an empty
 * body after a perfectly good response.
 */
export function updateProduct({ id, ...body }: UpdateProductInput): Promise<void> {
  return requestVoid(`/api/products/${id}`, { method: 'PUT', body: JSON.stringify(body) });
}
