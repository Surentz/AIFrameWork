import { render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { anAdminSession, server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { RequireRole } from './RequireRole';

function renderGuarded(): void {
  render(
    <MemoryRouter initialEntries={['/monitoring']}>
      <Routes>
        <Route path="/login" element={<p>the login page</p>} />
        <Route element={<RequireRole allow="Admin" />}>
          <Route path="/monitoring" element={<p>the monitoring page</p>} />
        </Route>
      </Routes>
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

describe('RequireRole', () => {
  it('renders the guarded route for a user with the role', async () => {
    server.use(http.get('/api/auth/me', () => HttpResponse.json(anAdminSession)));

    renderGuarded();

    expect(await screen.findByText('the monitoring page')).toBeInTheDocument();
  });

  it('explains the refusal rather than redirecting a signed-in user without the role', async () => {
    // The default handler answers with a Member session.
    renderGuarded();

    // Told, not bounced: reaching this route means the address was typed by hand, and a silent
    // redirect would leave the user unsure whether the page was missing, broken, or refused.
    expect(await screen.findByRole('alert')).toHaveTextContent('administrators');
    expect(screen.queryByText('the monitoring page')).not.toBeInTheDocument();
  });

  it('redirects to the login page when there is no session at all', async () => {
    server.use(
      http.get('/api/auth/me', () =>
        HttpResponse.json({ title: 'auth.failed', detail: 'Unauthorized.' }, { status: 401 }),
      ),
    );

    renderGuarded();

    expect(await screen.findByText('the login page')).toBeInTheDocument();
  });

  it('shows the failure rather than a refusal when the session check itself breaks', async () => {
    server.use(
      http.get('/api/auth/me', () =>
        HttpResponse.json(
          { title: 'server.error', detail: 'Something went wrong.' },
          { status: 500 },
        ),
      ),
    );

    renderGuarded();

    // A 500 is not "you may not be here". Reporting it as a refusal would send someone hunting
    // for a permission problem that does not exist.
    expect(await screen.findByRole('alert')).toHaveTextContent('Something went wrong.');
  });
});
