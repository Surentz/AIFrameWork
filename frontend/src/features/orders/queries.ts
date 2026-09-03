import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { UseMutationResult, UseQueryResult } from '@tanstack/react-query';
import { getOrder, listOrders, placeOrder } from '../../api/orders';
import type { ApiError } from '../../api/client';
import type { Order, OrderPage } from './types';

export const orderKeys = {
  all: ['orders'] as const,
  list: (cursor?: string) => [...orderKeys.all, 'list', cursor ?? null] as const,
  detail: (id: string) => [...orderKeys.all, 'detail', id] as const,
};

export function useOrders(cursor?: string): UseQueryResult<OrderPage, ApiError> {
  return useQuery({ queryKey: orderKeys.list(cursor), queryFn: () => listOrders({ cursor }) });
}

export function useOrder(id: string): UseQueryResult<Order, ApiError> {
  return useQuery({ queryKey: orderKeys.detail(id), queryFn: () => getOrder(id) });
}

export function usePlaceOrder(): UseMutationResult<
  string,
  ApiError,
  { sku: string; quantity: number }
> {
  const client = useQueryClient();

  return useMutation({
    mutationFn: placeOrder,
    onSuccess: async () => {
      // A new order changes the list; without this the user places one and does not see it.
      await client.invalidateQueries({ queryKey: orderKeys.all });
    },
  });
}
