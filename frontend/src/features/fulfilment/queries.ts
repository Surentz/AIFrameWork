import { useInfiniteQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import type { InfiniteData, UseInfiniteQueryResult, UseMutationResult } from '@tanstack/react-query';
import { listOrdersToFulfil, shipOrder } from '../../api/fulfilment';
import type { ApiError } from '../../api/client';
import { orderKeys } from '../orders/queries';
import type { FulfilmentOrderPage, OrderStatusChange } from './types';

export const fulfilmentKeys = {
  all: ['fulfilment'] as const,
  // The cursor is the page param, not part of the key — see orderKeys.list.
  queue: () => [...fulfilmentKeys.all, 'queue'] as const,
};

export function useOrdersToFulfil(): UseInfiniteQueryResult<
  InfiniteData<FulfilmentOrderPage>,
  ApiError
> {
  return useInfiniteQuery({
    queryKey: fulfilmentKeys.queue(),
    queryFn: ({ pageParam }) => listOrdersToFulfil({ cursor: pageParam }),
    initialPageParam: undefined as string | undefined,
    // null from the API means no more rows; TanStack Query reads undefined as done.
    getNextPageParam: (lastPage: FulfilmentOrderPage) => lastPage.nextCursor ?? undefined,
  });
}

export function useShipOrder(): UseMutationResult<OrderStatusChange, ApiError, string> {
  const client = useQueryClient();

  return useMutation({
    mutationFn: shipOrder,
    onSuccess: async () => {
      // A shipped order leaves the queue. The operator's own order reads are invalidated too,
      // for the one case where they shipped an order of their own.
      await Promise.all([
        client.invalidateQueries({ queryKey: fulfilmentKeys.all }),
        client.invalidateQueries({ queryKey: orderKeys.all }),
      ]);
    },
  });
}
