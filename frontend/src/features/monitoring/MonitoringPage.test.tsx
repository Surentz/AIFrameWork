import { render, screen } from '@testing-library/react';
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

    expect(await screen.findByText('Succeeded')).toBeInTheDocument();
    expect(screen.getByText('Dead-lettered')).toBeInTheDocument();
    expect(screen.getByText('12')).toBeInTheDocument();
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
});
