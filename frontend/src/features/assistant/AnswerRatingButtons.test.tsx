import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { AnswerRatingButtons } from './AnswerRatingButtons';
import type { AnswerRating } from './types';

function renderButtons(rating: AnswerRating | undefined, onRated = vi.fn()): { onRated: typeof onRated } {
  render(<AnswerRatingButtons conversationId="c1" turn={3} rating={rating} onRated={onRated} />, { wrapper: withQueryClient() });
  return { onRated };
}

describe('AnswerRatingButtons', () => {
  it('sends a rating and reports it', async () => {
    let body: unknown;
    server.use(
      http.put('/api/assistant/conversations/c1/turns/3/rating', async ({ request }) => {
        body = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const { onRated } = renderButtons(undefined);

    await userEvent.click(screen.getByRole('button', { name: 'Not helpful' }));

    expect(body).toEqual({ rating: 'Down' });
    expect(onRated).toHaveBeenCalledWith('Down');
  });

  it('clears the rating when the chosen one is clicked again', async () => {
    let body: unknown;
    server.use(
      http.put('/api/assistant/conversations/c1/turns/3/rating', async ({ request }) => {
        body = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const { onRated } = renderButtons('Up');

    expect(screen.getByRole('button', { name: 'Helpful' })).toHaveAttribute('aria-pressed', 'true');
    await userEvent.click(screen.getByRole('button', { name: 'Helpful' }));

    expect(body).toEqual({ rating: null });
    expect(onRated).toHaveBeenCalledWith(undefined);
  });

  it('shows the error when the rating fails', async () => {
    server.use(
      http.put('/api/assistant/conversations/c1/turns/3/rating', () =>
        HttpResponse.json({ status: 404, title: 'assistant.conversation_not_found', detail: 'That conversation does not exist or has expired.' }, { status: 404 }),
      ),
    );
    const { onRated } = renderButtons(undefined);

    await userEvent.click(screen.getByRole('button', { name: 'Helpful' }));

    expect(await screen.findByText(/does not exist or has expired/)).toBeInTheDocument();
    expect(onRated).not.toHaveBeenCalled();
  });
});
