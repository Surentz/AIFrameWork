import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type {
  InfiniteData,
  UseInfiniteQueryResult,
  UseMutationResult,
  UseQueryResult,
} from '@tanstack/react-query';
import { createProduct, getProduct, listProducts, updateProduct } from '../../api/products';
import type { CreateProductInput, UpdateProductInput } from '../../api/products';
import type { ApiError } from '../../api/client';
import type { Product, ProductPage } from './types';

export const productKeys = {
  all: ['products'] as const,
  // The cursor is the page param, not part of the key: every page of the list belongs to one
  // cache entry, so invalidating it refetches the whole accumulated list rather than orphaning
  // the pages already on screen.
  list: () => [...productKeys.all, 'list'] as const,
  detail: (id: string) => [...productKeys.all, 'detail', id] as const,
};

export function useProducts(): UseInfiniteQueryResult<InfiniteData<ProductPage>, ApiError> {
  return useInfiniteQuery({
    queryKey: productKeys.list(),
    queryFn: ({ pageParam }) => listProducts({ cursor: pageParam }),
    initialPageParam: undefined as string | undefined,
    // The API returns null when there are no more rows; TanStack Query reads undefined as done,
    // so the two have to be bridged or hasNextPage would stay true forever.
    getNextPageParam: (lastPage: ProductPage) => lastPage.nextCursor ?? undefined,
  });
}

export function useProduct(id: string | undefined): UseQueryResult<Product, ApiError> {
  return useQuery({
    queryKey: productKeys.detail(id ?? ''),
    // Never actually invoked while disabled, but must still type-check against Promise<Product>.
    queryFn: () => getProduct(id ?? ''),
    enabled: id !== undefined,
  });
}

export function useCreateProduct(): UseMutationResult<string, ApiError, CreateProductInput> {
  const client = useQueryClient();

  return useMutation({
    mutationFn: createProduct,
    onSuccess: async () => {
      // A new product changes the list; without this the user adds one and does not see it.
      await client.invalidateQueries({ queryKey: productKeys.all });
    },
  });
}

export function useUpdateProduct(): UseMutationResult<void, ApiError, UpdateProductInput> {
  const client = useQueryClient();

  return useMutation({
    mutationFn: updateProduct,
    onSuccess: async () => {
      // Invalidates the detail entry as well as the list: an edit changes the name and price
      // the list shows, not only the page the edit was made on.
      await client.invalidateQueries({ queryKey: productKeys.all });
    },
  });
}
