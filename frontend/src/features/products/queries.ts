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
import { useSession } from '../auth/queries';
import type { Product, ProductListItem, ProductPage } from './types';

/**
 * Whether the signed-in user may add or edit products — the SPA's mirror of the API's
 * `Catalogue.Manage` policy, named for the capability for the same reason (ADR 0025). Cosmetics:
 * it decides what to offer, and the API refuses a member with a 403 whether or not a link shows.
 * False while the session is still loading, so nothing is offered and then withdrawn.
 */
export function useCanManageCatalogue(): boolean {
  const { data: session } = useSession();
  return session?.role === 'Admin';
}

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

/**
 * The whole catalogue, for the order form's picker — a select needs every option, not a page.
 *
 * The paging loop lives in the queryFn rather than in an effect driving fetchNextPage, because
 * this repo does not fetch from useEffect (frontend/CLAUDE.md) and TanStack Query owns server
 * state. Its own cache entry, so it never collides with the paged list on the catalogue screen.
 *
 * A select stops being the right control somewhere in the hundreds of products. That is the
 * point to replace this with a search endpoint and an autocomplete, not to paginate the select.
 */
export function useAllProducts(): UseQueryResult<ProductListItem[], ApiError> {
  return useQuery({
    queryKey: [...productKeys.all, 'every'] as const,
    queryFn: async () => {
      const items: ProductListItem[] = [];
      let cursor: string | undefined;

      do {
        const page = await listProducts({ cursor, limit: 100 });
        items.push(...page.items);
        cursor = page.nextCursor ?? undefined;
      } while (cursor !== undefined);

      return items;
    },
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
