import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type {
  InfiniteData,
  UseInfiniteQueryResult,
  UseMutationResult,
  UseQueryResult,
} from '@tanstack/react-query';
import { getOrder, listOrders, placeOrder } from '../../api/orders';
import type { ApiError } from '../../api/client';
import type { Order, OrderPage } from './types';

export const orderKeys = {
  all: ['orders'] as const,
  // The cursor is the page param, not part of the key: every page of the list belongs to one
  // cache entry, so invalidating it refetches the whole accumulated list rather than orphaning
  // the pages already on screen.
  list: () => [...orderKeys.all, 'list'] as const,
  detail: (id: string) => [...orderKeys.all, 'detail', id] as const,
};

export function useOrders(): UseInfiniteQueryResult<InfiniteData<OrderPage>, ApiError> {
  return useInfiniteQuery({
    queryKey: orderKeys.list(),
    queryFn: ({ pageParam }) => listOrders({ cursor: pageParam }),
    initialPageParam: undefined as string | undefined,
    // The API returns null when there are no more rows; TanStack Query reads undefined as done,
    // so the two have to be bridged or hasNextPage would stay true forever.
    getNextPageParam: (lastPage: OrderPage) => lastPage.nextCursor ?? undefined,
  });
}

export function useOrder(id: string | undefined): UseQueryResult<Order, ApiError> {
  return useQuery({
    queryKey: orderKeys.detail(id ?? ''),
    // Never actually invoked while disabled, but must still type-check against Promise<Order>.
    queryFn: () => getOrder(id ?? ''),
    enabled: id !== undefined,
  });
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
