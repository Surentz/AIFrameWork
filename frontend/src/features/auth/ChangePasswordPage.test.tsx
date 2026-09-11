import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { ChangePasswordPage } from './ChangePasswordPage';

function renderPage(): void {
  render(
    <MemoryRouter>
      <ChangePasswordPage />
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

describe('ChangePasswordPage', () => {
  describe('sign out everywhere', () => {
    it('sends the request and clears the error state on success', async () => {
      let requestCount = 0;
      server.use(
        http.post('/api/auth/sign-out-everywhere', () => {
          requestCount += 1;
          return new HttpResponse(null, { status: 204 });
        }),
      );
      const user = userEvent.setup();
      renderPage();

      await user.click(screen.getByRole('button', { name: /sign out everywhere/i }));

      // Proves the button is actually wired to the endpoint, not just present in the DOM.
      await waitFor(() => {
        expect(requestCount).toBe(1);
      });
      await waitFor(() => {
        expect(screen.queryByRole('alert')).not.toBeInTheDocument();
      });
      expect(screen.getByRole('button', { name: /sign out everywhere/i })).not.toBeDisabled();
    });

    it('shows the error banner when the request fails', async () => {
      server.use(
        http.post('/api/auth/sign-out-everywhere', () =>
          HttpResponse.json(
            { title: 'server.error', detail: 'Something went wrong.' },
            { status: 500 },
          ),
        ),
      );
      const user = userEvent.setup();
      renderPage();

      await user.click(screen.getByRole('button', { name: /sign out everywhere/i }));

      expect(await screen.findByRole('alert')).toHaveTextContent('Something went wrong.');
    });
  });
});
