import { render, screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { LoginsPage } from './LoginsPage';

function renderPage(): void {
  render(
    <MemoryRouter>
      <LoginsPage />
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

describe('LoginsPage', () => {
  it('lists sign-in attempts with the outcome the endpoint itself never reveals', async () => {
    renderPage();

    const attempts = within(await screen.findByRole('table', { name: /sign-in attempts/i }));

    // The sign-in endpoint answers every failure identically; this table is the only place the
    // difference exists, which is the whole reason the attempted username is stored.
    expect(attempts.getByText('nosuchuser')).toBeInTheDocument();
    expect(attempts.getByText('UnknownUser')).toBeInTheDocument();
    expect(attempts.getByText('203.0.113.7')).toBeInTheDocument();
  });

  it('shows who is locked out', async () => {
    renderPage();

    const locked = within(await screen.findByRole('table', { name: /locked accounts/i }));

    expect(locked.getByText('grace')).toBeInTheDocument();
  });

  it('says how recently "online" means', async () => {
    renderPage();

    // A cookie session has no logout event, so "online" can only mean "seen recently". The page
    // has to say how recently, or the number means nothing.
    expect(await screen.findByText(/Online \(last 15 min\)/)).toBeInTheDocument();
  });

  it('surfaces a failed health read without taking the attempts table down', async () => {
    server.use(
      http.get('/api/monitoring/sign-ins/health', () =>
        HttpResponse.json(
          { title: 'server.error', detail: 'Something went wrong.' },
          { status: 500 },
        ),
      ),
    );

    renderPage();

    expect(await screen.findByRole('alert')).toHaveTextContent('Something went wrong.');
    expect(await screen.findByRole('table', { name: /sign-in attempts/i })).toBeInTheDocument();
  });
});
