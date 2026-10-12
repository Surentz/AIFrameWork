import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import type { UserRole } from './features/auth/types';
import { Sidebar } from './Sidebar';

interface Options {
  readonly role?: UserRole;
  readonly onNavigate?: () => void;
}

function renderSidebar(
  path: string,
  { role = 'Member', onNavigate = vi.fn() }: Options = {},
): void {
  render(
    <MemoryRouter initialEntries={[path]}>
      <Sidebar role={role} open={false} onNavigate={onNavigate} />
    </MemoryRouter>,
  );
}

const group = (name: string): HTMLElement => screen.getByRole('button', { name });

beforeEach(() => {
  window.localStorage.clear();
});
afterEach(() => {
  vi.restoreAllMocks();
});

describe('Sidebar', () => {
  it('labels its navigation "Main", apart from the assistant\'s "Conversations"', () => {
    renderSidebar('/');

    expect(screen.getByRole('navigation', { name: 'Main' })).toBeInTheDocument();
  });

  it('opens the group of the current page and folds the rest', () => {
    renderSidebar('/orders');

    expect(group('Sales')).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByRole('link', { name: 'Place an order' })).toBeInTheDocument();
    expect(group('Catalogue')).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByRole('link', { name: 'Products' })).not.toBeInTheDocument();
  });

  it('opens the group of a deep link, whichever page of it', () => {
    renderSidebar('/monitoring/jobs', { role: 'Admin' });

    expect(group('Operations')).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByRole('link', { name: 'Monitoring' })).toHaveAttribute(
      'aria-current',
      'page',
    );
  });

  it('opens the group of a deep link even when it was folded last time', () => {
    window.localStorage.setItem('aiframework.nav.open', JSON.stringify({ sales: false }));

    renderSidebar('/orders/exports');

    expect(group('Sales')).toHaveAttribute('aria-expanded', 'true');
  });

  it('marks only the exact page, so a sub-page does not light up the list', () => {
    renderSidebar('/orders/exports');

    expect(screen.getByRole('link', { name: 'Exports' })).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('link', { name: 'All orders' })).not.toHaveAttribute('aria-current');
  });

  it('marks the dashboard only at the root', () => {
    renderSidebar('/');
    expect(screen.getByRole('link', { name: 'Dashboard' })).toHaveAttribute('aria-current', 'page');
  });

  it('does not mark the dashboard elsewhere', () => {
    renderSidebar('/orders');
    expect(screen.getByRole('link', { name: 'Dashboard' })).not.toHaveAttribute('aria-current');
  });

  it('folds and unfolds a group on click', async () => {
    renderSidebar('/');

    await userEvent.click(group('Catalogue'));
    expect(group('Catalogue')).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByRole('link', { name: 'Products' })).toBeInTheDocument();

    await userEvent.click(group('Catalogue'));
    expect(group('Catalogue')).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByRole('link', { name: 'Products' })).not.toBeInTheDocument();
  });

  it('marks a folded group that holds the current page with a dot', async () => {
    renderSidebar('/orders');
    expect(document.querySelector('.sidebar__dot')).toBeNull();

    await userEvent.click(group('Sales'));

    expect(document.querySelector('.sidebar__dot')).not.toBeNull();
  });

  it('remembers what was opened when it is mounted again', async () => {
    renderSidebar('/');
    await userEvent.click(group('Catalogue'));
    cleanup();

    renderSidebar('/');

    expect(group('Catalogue')).toHaveAttribute('aria-expanded', 'true');
  });

  it('works when storage refuses', async () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new DOMException('denied', 'SecurityError');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('denied', 'SecurityError');
    });

    renderSidebar('/orders');
    expect(group('Sales')).toHaveAttribute('aria-expanded', 'true');

    await userEvent.click(group('Catalogue'));
    expect(group('Catalogue')).toHaveAttribute('aria-expanded', 'true');
  });

  it("gives a member no administrators' group and no way to their pages", () => {
    renderSidebar('/orders');

    expect(screen.queryByRole('button', { name: 'Operations' })).not.toBeInTheDocument();
    // hidden: true, so a link that is merely folded away would still be found. It is not in the DOM.
    expect(
      screen.queryByRole('link', { name: 'Fulfilment', hidden: true }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('link', { name: 'Monitoring', hidden: true }),
    ).not.toBeInTheDocument();
  });

  it('gives an administrator the operations group', () => {
    renderSidebar('/orders', { role: 'Admin' });

    expect(group('Operations')).toHaveAttribute('aria-expanded', 'false');
  });

  it('tells its host a link was followed, so the small-screen menu can close', async () => {
    const onNavigate = vi.fn();
    renderSidebar('/orders', { onNavigate });

    await userEvent.click(screen.getByRole('link', { name: 'Exports' }));

    expect(onNavigate).toHaveBeenCalledTimes(1);
  });

  it('links the brand to the dashboard', () => {
    renderSidebar('/orders');

    expect(screen.getByRole('link', { name: 'AI Framework' })).toHaveAttribute('href', '/');
  });
});
