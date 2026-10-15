import { ErrorPanel } from '../../components/ErrorPanel';
import { useRateAnswer } from './queries';
import type { AnswerRating } from './types';

interface AnswerRatingButtonsProps {
  readonly conversationId: string;
  readonly turn: number;
  readonly rating: AnswerRating | undefined;
  readonly onRated: (rating: AnswerRating | undefined) => void;
}

/** 👍/👎 on one saved answer. Clicking the chosen rating again clears it. */
export function AnswerRatingButtons({ conversationId, turn, rating, onRated }: AnswerRatingButtonsProps): React.JSX.Element {
  const rate = useRateAnswer();

  function choose(next: AnswerRating): void {
    const value = rating === next ? undefined : next;
    rate.mutate({ conversationId, turn, rating: value ?? null }, { onSuccess: () => { onRated(value); } });
  }

  return (
    <div className="assistant-rating" role="group" aria-label="Rate this answer">
      <button type="button" aria-label="Helpful" aria-pressed={rating === 'Up'} disabled={rate.isPending} onClick={() => { choose('Up'); }}>
        👍
      </button>
      <button type="button" aria-label="Not helpful" aria-pressed={rating === 'Down'} disabled={rate.isPending} onClick={() => { choose('Down'); }}>
        👎
      </button>
      {rate.error && <ErrorPanel error={rate.error} />}
    </div>
  );
}
