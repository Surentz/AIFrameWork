import type { AssistantSuggestions } from './types';

interface SuggestionsReplyProps {
  readonly suggestions: AssistantSuggestions;
  /** Asks a suggestion on the instant path: it is an intent's own question, so it is answered at once. */
  readonly onPick: (question: string) => void;
  /** Asks the original question of the AI. Absent when the AI is off. */
  readonly onAskAi?: (() => void) | undefined;
  readonly busy: boolean;
}

export function SuggestionsReply({
  suggestions,
  onPick,
  onAskAi,
  busy,
}: SuggestionsReplyProps): React.JSX.Element {
  return (
    <div className="quick-suggestions">
      <p className="assistant-badge">Not sure what you mean</p>
      <p>{suggestions.text}</p>
      {suggestions.options.length > 0 && (
        <div className="quick-suggestions__options">
          {suggestions.options.map((option) => (
            <button
              key={option}
              type="button"
              disabled={busy}
              onClick={() => {
                onPick(option);
              }}
            >
              {option}
            </button>
          ))}
        </div>
      )}
      {onAskAi && (
        <button className="quick-suggestions__ai" type="button" disabled={busy} onClick={onAskAi}>
          Ask the AI instead (takes a few seconds)
        </button>
      )}
    </div>
  );
}
