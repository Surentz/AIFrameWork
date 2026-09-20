import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { TrafficPage } from './TrafficPage';

function renderPage(): void {
  render(
    <MemoryRouter>
      <TrafficPage />
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

describe('TrafficPage', () => {
  it('leads with the rate, error rate and percentiles', async () => {
    renderPage();

    expect(await screen.findByText('12.5')).toBeInTheDocument();
    expect(screen.getByText('4.0%')).toBeInTheDocument();
    expect(screen.getByText('240 ms')).toBeInTheDocument();
  });

  it('draws requests and errors on one chart and latency on another', async () => {
    renderPage();

    // Two charts, not one with two y-scales: requests and errors share a unit, latency does not.
    // A dual axis is the single most common charting mistake, not a space saving.
    expect(await screen.findByRole('img', { name: /Requests and errors/ })).toBeInTheDocument();
    expect(screen.getByRole('img', { name: /Latency/ })).toBeInTheDocument();
  });

  it('keeps every plotted value reachable without hovering', async () => {
    renderPage();

    // The accessibility floor the chart ships with: a table view carrying the same numbers, so a
    // reader who cannot hover is not locked out of the data.
    const tables = await screen.findAllByText('Show as a table');

    expect(tables.length).toBeGreaterThanOrEqual(2);
  });

  it('breaks traffic down by endpoint', async () => {
    renderPage();

    const breakdown = within(await screen.findByRole('table', { name: /Traffic by name/i }));

    expect(breakdown.getByText('GET /api/orders')).toBeInTheDocument();
    expect(breakdown.getByText('180 ms')).toBeInTheDocument();
  });

  it('lets the window be narrowed', async () => {
    renderPage();

    const chooser = await screen.findByLabelText('Window');
    await userEvent.selectOptions(chooser, '15');

    expect(chooser).toHaveValue('15');
  });

  it('surfaces a failed read rather than an empty chart', async () => {
    server.use(
      http.get('/api/monitoring/traffic', () =>
        HttpResponse.json(
          { title: 'server.error', detail: 'Something went wrong.' },
          { status: 500 },
        ),
      ),
    );

    renderPage();

    expect(await screen.findByRole('alert')).toHaveTextContent('Something went wrong.');
  });
});
