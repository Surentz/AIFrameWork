import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { withQueryClient } from './test/withQueryClient';
import { AppLayout } from './AppLayout';

function renderShell(path = '/orders'): void {
  render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route element={<AppLayout />}>
          <Route path="*" element={<p>page content</p>} />
        </Route>
      </Routes>
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

const menuButton = (): HTMLElement => screen.getByRole('button', { name: 'Menu' });

beforeEach(() => {
  window.localStorage.clear();
});

describe('AppLayout', () => {
  it('renders the page beside the sidebar', () => {
    renderShell();

    expect(screen.getByRole('navigation', { name: 'Main' })).toBeInTheDocument();
    expect(screen.getByRole('main')).toHaveTextContent('page content');
  });

  it('keeps the signed-in user, the bell and Sign out in the top strip', async () => {
    renderShell();

    expect(await screen.findByText('Ada Lovelace')).toBeInTheDocument();
    expect(await screen.findByRole('link', { name: /^Notifications/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Sign out' })).toBeInTheDocument();
  });

  it('lets a full-bleed page, the assistant, fill the viewport', () => {
    renderShell('/assistant');

    expect(screen.getByRole('main')).toHaveClass('shell__content--full');
  });

  it('keeps an ordinary page in the padded column', () => {
    renderShell('/orders');

    expect(screen.getByRole('main')).not.toHaveClass('shell__content--full');
  });

  describe('the small-screen menu', () => {
    it('starts closed and opens from the menu button', async () => {
      renderShell();
      expect(menuButton()).toHaveAttribute('aria-expanded', 'false');
      expect(screen.queryByRole('button', { name: 'Close menu' })).not.toBeInTheDocument();

      await userEvent.click(menuButton());

      expect(menuButton()).toHaveAttribute('aria-expanded', 'true');
      expect(screen.getByRole('button', { name: 'Close menu' })).toBeInTheDocument();
    });

    it('moves focus into the menu when it opens, so the keyboard does not stay behind it', async () => {
      renderShell();

      await userEvent.click(menuButton());

      expect(screen.getByRole('link', { name: 'Dashboard' })).toHaveFocus();
    });

    it('closes from the scrim and gives focus back to the menu button', async () => {
      renderShell();
      await userEvent.click(menuButton());

      await userEvent.click(screen.getByRole('button', { name: 'Close menu' }));

      expect(menuButton()).toHaveAttribute('aria-expanded', 'false');
      expect(menuButton()).toHaveFocus();
    });

    it('closes on Escape and gives focus back to the menu button', async () => {
      renderShell();
      await userEvent.click(menuButton());
      // Focus is inside the menu here, not left on the button that opened it: that is what makes
      // the assertion below prove the Escape handler moved it back.
      expect(screen.getByRole('link', { name: 'Dashboard' })).toHaveFocus();

      await userEvent.keyboard('{Escape}');

      expect(menuButton()).toHaveAttribute('aria-expanded', 'false');
      expect(menuButton()).toHaveFocus();
    });

    it('closes when a link in it is followed, and gives focus back to the menu button', async () => {
      renderShell();
      await userEvent.click(menuButton());

      await userEvent.click(screen.getByRole('link', { name: 'Exports' }));

      expect(menuButton()).toHaveAttribute('aria-expanded', 'false');
      expect(menuButton()).toHaveFocus();
    });
  });
});
