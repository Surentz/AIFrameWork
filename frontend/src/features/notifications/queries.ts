import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type {
  InfiniteData,
  UseInfiniteQueryResult,
  UseMutationResult,
  UseQueryResult,
} from '@tanstack/react-query';
import {
  getUnreadCount,
  listNotifications,
  markAllNotificationsRead,
  markNotificationRead,
} from '../../api/notifications';
import type { ApiError } from '../../api/client';
import type { NotificationPage, NotificationReadResult, UnreadCount } from './types';

/**
 * How often the badge re-asks the server, in milliseconds.
 *
 * Polling, not a push, and that is a deliberate gap rather than an oversight: the backend can
 * push over SignalR (`/hubs/notifications`, ADR 0016), but it is off unless `Realtime__Enabled`
 * is set, and nothing here speaks that protocol yet. Until it does, this interval is the only
 * thing that makes a notification raised by the outbox appear without a reload — the feed is
 * written by a background pump, so no request the user makes will ever return it as a side
 * effect.
 *
 * Thirty seconds matches the TTL the cached reads elsewhere use (ADR 0009): frequent enough to
 * feel live, rare enough that an idle tab is not a load generator.
 */
const UnreadPollIntervalMs = 30_000;

export const notificationKeys = {
  all: ['notifications'] as const,
  // The cursor is the page param, not part of the key — same reasoning as orderKeys.list().
  // unreadOnly IS part of it: the two filters are genuinely different lists, and sharing one
  // entry would show read rows in the unread view after an invalidation.
  list: (unreadOnly: boolean) => [...notificationKeys.all, 'list', { unreadOnly }] as const,
  unreadCount: () => [...notificationKeys.all, 'unread-count'] as const,
};

export function useNotifications(
  unreadOnly = false,
): UseInfiniteQueryResult<InfiniteData<NotificationPage>, ApiError> {
  return useInfiniteQuery({
    queryKey: notificationKeys.list(unreadOnly),
    queryFn: ({ pageParam }) => listNotifications({ cursor: pageParam, unreadOnly }),
    initialPageParam: undefined as string | undefined,
    // The API returns null when there are no more rows; TanStack Query reads undefined as done.
    getNextPageParam: (lastPage: NotificationPage) => lastPage.nextCursor ?? undefined,
  });
}

export function useUnreadCount(): UseQueryResult<UnreadCount, ApiError> {
  return useQuery({
    queryKey: notificationKeys.unreadCount(),
    queryFn: getUnreadCount,
    refetchInterval: UnreadPollIntervalMs,
    // The default, stated explicitly because it is a decision rather than an oversight: false
    // means the interval fires only while the tab is focused (query-core checks
    // `refetchIntervalInBackground || focusManager.isFocused()` before fetching). A backgrounded
    // tab polling for a badge nobody is looking at is a load generator; it catches up on the
    // refetch that focus itself triggers.
    refetchIntervalInBackground: false,
  });
}

export function useMarkNotificationRead(): UseMutationResult<
  NotificationReadResult,
  ApiError,
  string
> {
  const client = useQueryClient();

  return useMutation({
    mutationFn: markNotificationRead,
    onSuccess: async () => {
      // Both lists and the badge: marking one read removes it from the unread view, changes its
      // appearance in the full view, and decrements the count. Invalidating the whole `all` key
      // covers every one of those without naming them individually.
      await client.invalidateQueries({ queryKey: notificationKeys.all });
    },
  });
}

export function useMarkAllNotificationsRead(): UseMutationResult<
  NotificationReadResult,
  ApiError,
  void
> {
  const client = useQueryClient();

  return useMutation({
    mutationFn: markAllNotificationsRead,
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: notificationKeys.all });
    },
  });
}
