import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { AssistantInsightsPage } from './AssistantInsightsPage';

const insights = {
  since: '2026-10-08T09:00:00Z',
  days: [{ day: '2026-10-14', instant: 6, help: 2, suggested: 1, ai: 1 }],
  targets: [{ path: 'Instant', targetId: 'order_status', questions: 6, up: 2, down: 1 }],
  misses: [
    {
      at: '2026-10-14T10:00:00Z',
      path: 'Suggested',
      targetId: null,
      bestScore: 0.61,
      rating: null,
      question: 'Write me a poem about the rain',
    },
  ],
};

function renderPage(): void {
  render(
    <MemoryRouter>
      <AssistantInsightsPage />
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

describe('AssistantInsightsPage', () => {
  it('shows the answer rate per day', async () => {
    server.use(http.get('/api/monitoring/assistant/insights', () => HttpResponse.json(insights)));
    renderPage();

    const days = await screen.findByRole('table', { name: 'Answers by day' });
    // 6 instant + 2 help of 10 answered at once
    expect(within(days).getByRole('row', { name: /2026-10-14/ })).toHaveTextContent('80%');
  });

  it('lists the missed questions', async () => {
    server.use(http.get('/api/monitoring/assistant/insights', () => HttpResponse.json(insights)));
    renderPage();

    const misses = await screen.findByRole('table', { name: 'Missed questions' });
    expect(within(misses).getByText('Write me a poem about the rain')).toBeInTheDocument();
    expect(screen.getByText(/up to 50 missed questions, rated not helpful first/)).toBeInTheDocument();
  });

  it('asks for the chosen window', async () => {
    const asked: string[] = [];
    server.use(
      http.get('/api/monitoring/assistant/insights', ({ request }) => {
        asked.push(new URL(request.url).searchParams.get('days') ?? '');
        return HttpResponse.json(insights);
      }),
    );
    renderPage();
    await screen.findByRole('table', { name: 'Answers by day' });

    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Window' }), '30');

    await screen.findByRole('table', { name: 'Answers by day' });
    expect(asked).toContain('30');
  });

  it('says so when there is nothing yet', async () => {
    server.use(
      http.get('/api/monitoring/assistant/insights', () =>
        HttpResponse.json({ ...insights, days: [], targets: [], misses: [] }),
      ),
    );
    renderPage();

    expect(await screen.findByText('No questions in this window.')).toBeInTheDocument();
  });

  it('shows the error when the insights cannot load', async () => {
    server.use(
      http.get('/api/monitoring/assistant/insights', () =>
        HttpResponse.json({ status: 503, title: 'unavailable', detail: 'Down.' }, { status: 503 }),
      ),
    );
    renderPage();

    expect(await screen.findByText(/Down\./)).toBeInTheDocument();
  });
});
