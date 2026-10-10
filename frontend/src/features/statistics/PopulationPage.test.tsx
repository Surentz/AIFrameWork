import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { PopulationPage } from './PopulationPage';

function renderPage(): void {
  render(<PopulationPage />, { wrapper: withQueryClient() });
}

const picker = (): Promise<HTMLElement> => screen.findByRole('combobox', { name: 'Area' });

describe('PopulationPage', () => {
  it('starts on all of Denmark', async () => {
    renderPage();

    expect(await picker()).toHaveValue('000');
    expect(await screen.findByRole('figure', { name: /All Denmark/ })).toHaveTextContent(
      '6.013.891',
    );
  });

  it('offers every published area to choose from', async () => {
    renderPage();

    const options = within(await picker()).getAllByRole('option');
    expect(options.map((option) => option.textContent)).toEqual([
      'All Denmark',
      'Region Hovedstaden',
      'Copenhagen',
    ]);
  });

  it('shows the chosen area’s figure and the quarter it is for', async () => {
    renderPage();

    await userEvent.selectOptions(await picker(), '101');

    expect(
      await screen.findByRole('figure', { name: /Copenhagen, first day of 2026Q3/ }),
    ).toHaveTextContent('670.389');
  });

  it('credits the source under the figure, as its licence requires', async () => {
    renderPage();

    expect(await screen.findByText('Source: Statistics Denmark (CC BY 4.0)')).toBeInTheDocument();
  });

  it('says so when the chosen area has no published figure, and still lets the user pick another', async () => {
    server.use(
      http.get('/api/statistics/population', ({ request }) =>
        new URL(request.url).searchParams.get('area') === '084'
          ? HttpResponse.json(
              {
                title: 'statistics.area_not_found',
                detail: 'No population figures are published for this area.',
                status: 404,
              },
              { status: 404 },
            )
          : undefined,
      ),
    );
    renderPage();

    await userEvent.selectOptions(await picker(), '084');

    expect(await screen.findByRole('alert')).toHaveTextContent(
      /No population figures are published/,
    );
    expect(screen.getByRole('combobox', { name: 'Area' })).toBeInTheDocument();
  });

  it('renders the error when the source is unavailable', async () => {
    server.use(
      http.get('/api/statistics/population', () =>
        HttpResponse.json(
          {
            title: 'external_system.unavailable',
            detail: 'StatisticsDenmark is unavailable.',
            status: 503,
          },
          { status: 503, headers: { 'Retry-After': '30' } },
        ),
      ),
    );

    renderPage();

    expect(await screen.findByRole('alert')).toHaveTextContent(/unavailable/i);
  });

  it('renders the error when the areas cannot be loaded', async () => {
    server.use(
      http.get('/api/statistics/population/areas', () =>
        HttpResponse.json({ title: 'external_system.unavailable', status: 503 }, { status: 503 }),
      ),
    );

    renderPage();

    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(screen.queryByRole('combobox', { name: 'Area' })).not.toBeInTheDocument();
  });
});
