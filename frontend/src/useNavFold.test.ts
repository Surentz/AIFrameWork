import { act, renderHook } from '@testing-library/react';
import { useNavFold } from './useNavFold';

const storageKey = 'aiframework.nav.open';

beforeEach(() => {
  window.localStorage.clear();
});
afterEach(() => {
  vi.restoreAllMocks();
});

describe('useNavFold', () => {
  it('starts with only the group holding the current page open', () => {
    const { result } = renderHook(() => useNavFold('sales'));

    expect(result.current.isOpen('sales')).toBe(true);
    expect(result.current.isOpen('catalogue')).toBe(false);
  });

  it('starts with every group closed on a page that belongs to none', () => {
    const { result } = renderHook(() => useNavFold(undefined));

    expect(result.current.isOpen('sales')).toBe(false);
  });

  it('folds and unfolds a group', () => {
    const { result } = renderHook(() => useNavFold('sales'));

    act(() => {
      result.current.toggle('sales');
    });
    expect(result.current.isOpen('sales')).toBe(false);

    act(() => {
      result.current.toggle('sales');
    });
    expect(result.current.isOpen('sales')).toBe(true);
  });

  it('remembers what was opened across mounts', () => {
    const first = renderHook(() => useNavFold(undefined));
    act(() => {
      first.result.current.toggle('catalogue');
    });
    first.unmount();

    const second = renderHook(() => useNavFold(undefined));

    expect(second.result.current.isOpen('catalogue')).toBe(true);
  });

  it('does not remember a group that was only opened by being on a page inside it', () => {
    const first = renderHook(() => useNavFold('sales'));
    first.unmount();

    const second = renderHook(() => useNavFold(undefined));

    expect(second.result.current.isOpen('sales')).toBe(false);
  });

  it('remembers only what the user toggled, not the groups navigation opened around it', () => {
    const first = renderHook(({ id }) => useNavFold(id), { initialProps: { id: 'sales' } });
    first.rerender({ id: 'insights' });
    act(() => {
      first.result.current.toggle('catalogue');
    });
    first.unmount();

    const second = renderHook(() => useNavFold(undefined));

    expect(second.result.current.isOpen('catalogue')).toBe(true);
    expect(second.result.current.isOpen('sales')).toBe(false);
    expect(second.result.current.isOpen('insights')).toBe(false);
  });

  it('opens the group of a page navigated to, even if it was folded before', () => {
    const { result, rerender } = renderHook(({ id }) => useNavFold(id), {
      initialProps: { id: 'sales' },
    });
    act(() => {
      result.current.toggle('sales');
    });
    expect(result.current.isOpen('sales')).toBe(false);

    rerender({ id: 'catalogue' });
    expect(result.current.isOpen('catalogue')).toBe(true);
    expect(result.current.isOpen('sales')).toBe(false);

    rerender({ id: 'sales' });
    expect(result.current.isOpen('sales')).toBe(true);
  });

  it('leaves a group the user folded alone while they stay on a page inside it', () => {
    const { result, rerender } = renderHook(({ id }) => useNavFold(id), {
      initialProps: { id: 'sales' },
    });
    act(() => {
      result.current.toggle('sales');
    });

    rerender({ id: 'sales' });

    expect(result.current.isOpen('sales')).toBe(false);
  });

  it.each(['{not json', '[1,2]', '{"sales":"yes"}', 'null', '"text"'])(
    'survives unreadable or hostile stored state: %s',
    (raw) => {
      window.localStorage.setItem(storageKey, raw);

      const { result } = renderHook(() => useNavFold('account'));

      expect(result.current.isOpen('account')).toBe(true);
      expect(result.current.isOpen('sales')).toBe(false);
    },
  );

  it('works with storage that refuses every read and write', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new DOMException('denied', 'SecurityError');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('denied', 'SecurityError');
    });

    const { result } = renderHook(() => useNavFold('sales'));
    expect(result.current.isOpen('sales')).toBe(true);

    act(() => {
      result.current.toggle('catalogue');
    });
    expect(result.current.isOpen('catalogue')).toBe(true);
  });
});
