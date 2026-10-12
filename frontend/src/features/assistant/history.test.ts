import type { ConversationSummary } from './types';
import { groupByAge } from './history';

const now = new Date('2026-10-11T15:00:00');
const at = (iso: string): ConversationSummary => ({ id: iso, title: iso, lastActiveAt: new Date(iso).toISOString() });

describe('groupByAge', () => {
  it('puts today, the previous 7 days and older into their groups, in order', () => {
    const groups = groupByAge(
      [at('2026-10-11T09:00:00'), at('2026-10-08T09:00:00'), at('2026-09-20T09:00:00')],
      now,
    );

    expect(groups.map((g) => [g.label, g.items.length])).toEqual([
      ['Today', 1],
      ['Previous 7 days', 1],
      ['Previous 30 days', 1],
    ]);
  });

  it('starts today at local midnight', () => {
    const groups = groupByAge([at('2026-10-10T23:59:00')], now);

    expect(groups[0]?.label).toBe('Previous 7 days');
  });

  it('leaves out empty groups', () => {
    expect(groupByAge([at('2026-10-11T09:00:00')], now).map((g) => g.label)).toEqual(['Today']);
  });
});
