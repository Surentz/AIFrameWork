import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { JobsPage } from './JobsPage';

function renderPage(): void {
  render(
    <MemoryRouter>
      <JobsPage />
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

describe('JobsPage', () => {
  it('lists job runs with their outcome and error', async () => {
    renderPage();

    // Scoped to the runs table: the same job name also appears in the dead-letter table below,
    // which is the point of both being on one page.
    const runs = within(await screen.findByRole('table', { name: /job runs/i }));

    expect(runs.getByText('RebuildOrderReport')).toBeInTheDocument();
    expect(runs.getByText('Failed')).toBeInTheDocument();
    expect(runs.getByText('InvalidOperationException: the handler gave up')).toBeInTheDocument();
  });

  it('shows the attempt number, so a retry reads as a retry', async () => {
    renderPage();

    // The whole reason job_runs is keyed on (EnvelopeId, Attempt): three failures of one job must
    // read as one job retried twice, not as three separate jobs.
    const runs = within(await screen.findByRole('table', { name: /job runs/i }));
    const row = runs.getByText('RebuildOrderReport').closest('tr');

    expect(row).not.toBeNull();
    expect(row).toHaveTextContent('2');
  });

  it('retries a dead letter and refreshes the list', async () => {
    let retried: string | null = null;
    server.use(
      http.post('/api/monitoring/jobs/dead-letters/:id/retry', ({ params }) => {
        retried = String(params.id);
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderPage();

    await userEvent.click(await screen.findByRole('button', { name: 'Retry' }));

    await waitFor(() => {
      expect(retried).toBe('88888888-8888-8888-8888-888888888888');
    });
  });

  it('surfaces a failed retry rather than swallowing it', async () => {
    server.use(
      http.post('/api/monitoring/jobs/dead-letters/:id/retry', () =>
        HttpResponse.json(
          { title: 'dead_letter.not_found', detail: 'No dead-lettered message with that id.' },
          { status: 404 },
        ),
      ),
    );

    renderPage();

    await userEvent.click(await screen.findByRole('button', { name: 'Retry' }));

    // An operator who clicked retry on a message someone else already discarded must be told,
    // not left watching for a run that never appears.
    expect(await screen.findByRole('alert')).toHaveTextContent('No dead-lettered message');
  });

  it('offers only the jobs that can actually be triggered', async () => {
    renderPage();

    // Awaited on the OPTION, not the select: the select renders immediately with only its
    // placeholder and fills in once job health resolves.
    await screen.findByRole('option', { name: 'PruneProcessedOutbox' });
    const chooser = screen.getByLabelText('Job', { selector: 'select' });

    expect(chooser).toHaveTextContent('PruneProcessedOutbox');
    // A job that takes arguments has none a trigger could supply, so the API never lists it.
    expect(chooser).not.toHaveTextContent('RebuildOrderReport');
  });

  it('triggers the chosen job', async () => {
    let triggered: string | null = null;
    server.use(
      http.post('/api/monitoring/jobs/trigger', async ({ request }) => {
        const body = (await request.json()) as { jobName: string };
        triggered = body.jobName;
        return new HttpResponse(null, { status: 202 });
      }),
    );

    renderPage();

    await screen.findByRole('option', { name: 'PruneJobRuns' });
    const chooser = screen.getByLabelText('Job', { selector: 'select' });
    await userEvent.selectOptions(chooser, 'PruneJobRuns');
    await userEvent.click(screen.getByRole('button', { name: 'Run now' }));

    await waitFor(() => {
      expect(triggered).toBe('PruneJobRuns');
    });
  });
});
