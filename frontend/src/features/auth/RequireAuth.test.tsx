import { render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { RequireAuth } from './RequireAuth';

function renderGuarded(): void {
  render(
    <MemoryRouter initialEntries={['/orders']}>
      <Routes>
        <Route path="/login" element={<p>the login page</p>} />
        <Route element={<RequireAuth />}>
          <Route path="/orders" element={<p>the orders page</p>} />
        </Route>
      </Routes>
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

describe('RequireAuth', () => {
  it('renders the guarded route when there is a session', async () => {
    // The default handler in test/handlers.ts answers /api/auth/me with a session.
    renderGuarded();

    expect(await screen.findByText('the orders page')).toBeInTheDocument();
  });

  it('redirects to the login page when there is no session', async () => {
    server.use(
      http.get('/api/auth/me', () =>
        HttpResponse.json({ title: 'auth.failed', detail: 'Unauthorized.' }, { status: 401 }),
      ),
    );

    renderGuarded();

    expect(await screen.findByText('the login page')).toBeInTheDocument();
    expect(screen.queryByText('the orders page')).not.toBeInTheDocument();
  });

  it('shows the failure rather than the login page when the session check itself breaks', async () => {
    server.use(
      http.get('/api/auth/me', () =>
        HttpResponse.json(
          { title: 'server.error', detail: 'Something went wrong.' },
          { status: 500 },
        ),
      ),
    );

    renderGuarded();

    // A 500 is not "signed out". Redirecting would send the user to a login page that cannot
    // help them and would hide the real failure.
    expect(await screen.findByRole('alert')).toHaveTextContent('Something went wrong.');
    expect(screen.queryByText('the login page')).not.toBeInTheDocument();
  });
});
