import type {
  NotificationPage,
  NotificationReadResult,
  UnreadCount,
} from '../features/notifications/types';
import { request } from './client';

// The `| undefined` on each member is required, not noise: exactOptionalPropertyTypes is on, so
// `{ cursor }` where cursor is `string | undefined` does not satisfy a bare `cursor?: string`.
// Same shape as api/orders.ts.
export function listNotifications(params: {
  cursor?: string | undefined;
  limit?: number | undefined;
  unreadOnly?: boolean | undefined;
}): Promise<NotificationPage> {
  const search = new URLSearchParams();
  search.set('limit', String(params.limit ?? 20));
  if (params.cursor !== undefined) {
    search.set('cursor', params.cursor);
  }
  if (params.unreadOnly === true) {
    search.set('unreadOnly', 'true');
  }

  return request<NotificationPage>(`/api/notifications?${search.toString()}`);
}

export function getUnreadCount(): Promise<UnreadCount> {
  return request<UnreadCount>('/api/notifications/unread-count');
}

// POST with no body: the id is in the path and there is nothing else to send. `request` rather
// than the 204 helper because both mark-read endpoints answer 200 with the new counts, which is
// what lets the bell update without a second round-trip.
export function markNotificationRead(id: string): Promise<NotificationReadResult> {
  return request<NotificationReadResult>(`/api/notifications/${id}/read`, { method: 'POST' });
}

export function markAllNotificationsRead(): Promise<NotificationReadResult> {
  return request<NotificationReadResult>('/api/notifications/read-all', { method: 'POST' });
}
