import { render, screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { IntegrationsPage } from './IntegrationsPage';

function renderPage(): void {
  render(
    <MemoryRouter>
      <IntegrationsPage />
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

function rowFor(name: string): HTMLElement {
  const cell = screen.getByRole('rowheader', { name });
  const row = cell.closest('tr');
  if (!row) {
    throw new Error(`no row for ${name}`);
  }
  return row;
}

describe('IntegrationsPage', () => {
  it('lists each system with its status and last hour of calls', async () => {
    renderPage();

    await screen.findByRole('rowheader', { name: 'PartnerSimulator' });
    expect(within(rowFor('PartnerSimulator')).getByText('Healthy')).toBeInTheDocument();
    expect(within(rowFor('PartnerSimulator')).getByText('120')).toBeInTheDocument();
    expect(within(rowFor('Unreachable')).getByText('Unhealthy')).toBeInTheDocument();
  });

  it('IntegrationsPage_ForAStaleRow_SaysStaleNotHealthy', async () => {
    server.use(
      http.get('/api/monitoring/external-systems', () =>
        HttpResponse.json({
          trafficSince: '2026-10-09T11:00:00+00:00',
          systems: [{
            name: 'Partner', state: 'Healthy', description: 'ok', checkedAt: '2026-10-09T10:00:00+00:00',
            stale: true, certificateNotAfter: null, tokenOk: null,
            calls: 0, failed: 0, faulted: 0, attempts: 0, p95Ms: null,
          }],
        }),
      ),
    );

    renderPage();

    const row = await screen.findByRole('rowheader', { name: 'Partner' });
    expect(within(row.closest('tr') ?? document.body).getByText(/Stale/)).toBeInTheDocument();
    expect(within(row.closest('tr') ?? document.body).queryByText('Healthy')).not.toBeInTheDocument();
  });

  it('flags a certificate expiring within thirty days', async () => {
    const soon = new Date(Date.now() + 10 * 24 * 60 * 60 * 1000).toISOString();
    server.use(
      http.get('/api/monitoring/external-systems', () =>
        HttpResponse.json({
          trafficSince: '2026-10-09T11:00:00+00:00',
          systems: [{
            name: 'Partner', state: 'Degraded', description: 'expires soon', checkedAt: new Date().toISOString(),
            stale: false, certificateNotAfter: soon, tokenOk: true,
            calls: 0, failed: 0, faulted: 0, attempts: 0, p95Ms: null,
          }],
        }),
      ),
    );

    renderPage();

    expect(await screen.findByText(/in (9|10) days/)).toHaveClass('attention');
  });

  it('says so when no external system is configured', async () => {
    server.use(
      http.get('/api/monitoring/external-systems', () =>
        HttpResponse.json({ trafficSince: '2026-10-09T11:00:00+00:00', systems: [] }),
      ),
    );

    renderPage();

    expect(await screen.findByText(/No external system is configured/)).toBeInTheDocument();
  });

  it('renders the error state', async () => {
    server.use(
      http.get('/api/monitoring/external-systems', () =>
        HttpResponse.json({ title: 'Boom', status: 500 }, { status: 500 }),
      ),
    );

    renderPage();

    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });
});
