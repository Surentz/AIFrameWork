import { render, screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { MonitoringPage } from './MonitoringPage';

function renderPage(): void {
  render(
    <MemoryRouter>
      <MonitoringPage />
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

describe('MonitoringPage', () => {
  it('shows the job health tiles', async () => {
    renderPage();

    // Scoped: the overview now carries three tile groups, and "Succeeded" means something
    // different in the jobs group and the sign-ins one.
    const jobs = within(await screen.findByRole('list', { name: 'Job health' }));

    expect(jobs.getByText('Succeeded')).toBeInTheDocument();
    expect(jobs.getByText('Dead-lettered')).toBeInTheDocument();
    expect(jobs.getByText('12')).toBeInTheDocument();
  });

  it('shows the sign-in and traffic tiles, each linking onward', async () => {
    renderPage();

    const signIns = within(await screen.findByRole('list', { name: 'Sign-in health' }));
    expect(signIns.getByText('Online now')).toBeInTheDocument();

    const traffic = within(screen.getByRole('list', { name: 'Traffic' }));
    expect(traffic.getByText('Requests per minute')).toBeInTheDocument();

    expect(
      screen.getByRole('link', { name: /Request rates, latency/ }),
    ).toBeInTheDocument();
  });

  it('links to the jobs drill-down', async () => {
    renderPage();

    expect(
      await screen.findByRole('link', { name: 'Job runs and dead letters' }),
    ).toBeInTheDocument();
  });

  it('renders the tiles even when job health is unavailable', async () => {
    server.use(
      http.get('/api/monitoring/jobs/health', () =>
        HttpResponse.json({ title: 'server.error', detail: 'Something went wrong.' }, { status: 500 }),
      ),
    );

    renderPage();

    // The page still identifies the operator and links onward; only the tiles are missing. A
    // failed panel must not take the whole page down with it.
    expect(await screen.findByRole('alert')).toHaveTextContent('Something went wrong.');
    expect(screen.getByRole('link', { name: 'Job runs and dead letters' })).toBeInTheDocument();
  });

  it('shows one status per external system and links to the integrations page', async () => {
    renderPage();

    const systems = within(await screen.findByRole('list', { name: 'External systems' }));
    expect(systems.getByText(/PartnerSimulator/)).toBeInTheDocument();
    expect(systems.getByText(/Unhealthy/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /External system health and traffic/ })).toHaveAttribute(
      'href',
      '/monitoring/integrations',
    );
  });
});
