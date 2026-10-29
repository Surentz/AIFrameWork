import { groupForPath, navGroups, visibleGroups } from './navigation';

const memberIds = ['sales', 'catalogue', 'insights', 'account'];
const adminIds = ['sales', 'catalogue', 'insights', 'operations', 'account'];

describe('navigation', () => {
  it('shows a member every group except the administrators', () => {
    expect(visibleGroups('Member').map((group) => group.id)).toEqual(memberIds);
  });

  it('shows an administrator every group', () => {
    expect(visibleGroups('Admin').map((group) => group.id)).toEqual(adminIds);
  });

  it('treats a session that has not loaded as a member', () => {
    expect(visibleGroups(undefined).map((group) => group.id)).toEqual(memberIds);
  });

  it('gives every group at least one page', () => {
    for (const group of navGroups) {
      expect(group.items.length, group.id).toBeGreaterThan(0);
    }
  });

  it('never lists the same address twice', () => {
    const addresses = navGroups.flatMap((group) => group.items.map((item) => item.to));
    expect(new Set(addresses).size).toBe(addresses.length);
  });

  it.each([
    ['/orders', 'sales'],
    ['/orders/new', 'sales'],
    ['/orders/exports', 'sales'],
    ['/orders/42', 'sales'],
    ['/map', 'sales'],
    ['/products', 'catalogue'],
    ['/products/new', 'catalogue'],
    ['/products/7/edit', 'catalogue'],
    ['/statistics/population', 'insights'],
    ['/fulfilment', 'operations'],
    ['/monitoring', 'operations'],
    ['/monitoring/jobs', 'operations'],
    ['/account/password', 'account'],
  ])('puts %s in the %s group', (path, id) => {
    expect(groupForPath(path)?.id).toBe(id);
  });

  it.each(['/', '/notifications', '/assistant', '/assistant/abc', '/nowhere', '/ordersx'])(
    'puts %s in no group',
    (path) => {
      expect(groupForPath(path)).toBeUndefined();
    },
  );
});
