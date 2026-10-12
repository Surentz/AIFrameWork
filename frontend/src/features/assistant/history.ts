import type { ConversationSummary } from './types';

export interface ConversationGroup {
  readonly label: 'Today' | 'Previous 7 days' | 'Previous 30 days';
  readonly items: readonly ConversationSummary[];
}

const DayMs = 24 * 60 * 60 * 1000;

/**
 * The sidebar's groups, by last activity in the viewer's own time zone. The server keeps 30 days,
 * so "Previous 30 days" is everything older than a week. Empty groups are left out; the input's
 * newest-first order is kept within each group.
 */
export function groupByAge(
  conversations: readonly ConversationSummary[],
  now: Date,
): ConversationGroup[] {
  const startOfToday = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
  const today: ConversationSummary[] = [];
  const week: ConversationSummary[] = [];
  const older: ConversationSummary[] = [];

  for (const conversation of conversations) {
    const at = Date.parse(conversation.lastActiveAt);
    if (at >= startOfToday) {
      today.push(conversation);
    } else if (at >= startOfToday - 7 * DayMs) {
      week.push(conversation);
    } else {
      older.push(conversation);
    }
  }

  const groups: ConversationGroup[] = [
    { label: 'Today', items: today },
    { label: 'Previous 7 days', items: week },
    { label: 'Previous 30 days', items: older },
  ];
  return groups.filter((group) => group.items.length > 0);
}
