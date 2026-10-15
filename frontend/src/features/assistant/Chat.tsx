import { ApiError } from '../../api/client';
import { ErrorPanel } from '../../components/ErrorPanel';
import { AnswerRatingButtons } from './AnswerRatingButtons';
import { AssistantMarkdown } from './AssistantMarkdown';
import { Composer } from './Composer';
import type { AssistantMessage, ConversationState } from './conversation';
import { QuickAnswerCard } from './QuickAnswerCard';
import { SuggestionsReply } from './SuggestionsReply';
import { useAssistantConversation } from './useAssistantConversation';

/** What each tool looked at, in the words its chip shows. */
const toolLabels: Readonly<Record<string, string>> = {
  my_orders: 'your orders',
  get_order: 'an order',
  my_exports: 'your exports',
  list_products: 'the catalogue',
  population: 'population figures',
  job_health: 'job health',
  list_job_runs: 'job runs',
  get_job_run: 'a job run',
  list_dead_letters: 'dead letters',
  traffic_summary: 'traffic',
  traffic_series: 'traffic over time',
  external_systems: 'external systems',
  sign_in_health: 'sign-ins',
};

const memberSuggestions = ['Where is my latest order?', 'Which of my exports are ready?'];

interface ChatProps {
  /** A saved conversation to continue (fromSaved), or undefined for a new chat. */
  readonly initial?: ConversationState | undefined;
  readonly onSaved: (conversationId: string) => void;
  readonly operator: boolean;
  /** The composer's text, held by the page so it survives New chat. */
  readonly draft: string;
  readonly onDraftChange: (draft: string) => void;
  /** Instant answers are on: questions go to the quick path first. */
  readonly quickEnabled?: boolean | undefined;
  /** The AI is on: a not-sure reply offers "Ask the AI instead". */
  readonly aiEnabled?: boolean | undefined;
}

/** One conversation: the messages, centred, and the composer pinned under them. */
export function Chat({ initial, onSaved, operator,
  draft,
  onDraftChange,
  quickEnabled = false,
  aiEnabled = true,
}: ChatProps): React.JSX.Element {
  const conversation = useAssistantConversation({ initial, onSaved });
  const suggestions = [
    ...memberSuggestions,
    operator ? 'Did any background job fail today?' : 'Show me the catalogue',
  ];

  function send(): void {
    const question = draft.trim();
    onDraftChange('');
    void conversation.ask(question, quickEnabled ? 'quick' : 'ai').then((outcome) => {
      // Nothing was saved for a failed question, so put it back: asking again needs no retyping.
      if (outcome === 'failed') {
        onDraftChange(question);
      }
    });
  }

  return (
    <section className="assistant-chat" aria-label="Chat">
      <div className="assistant-chat__scroll">
        <div className="assistant-chat__column">
          {conversation.messages.length === 0 ? (
            <div className="assistant-empty">
              <h2>How can I help you today?</h2>
              <div className="assistant-empty__suggestions">
                {suggestions.map((suggestion) => (
                  <button key={suggestion} type="button" onClick={() => { onDraftChange(suggestion); }}>
                    {suggestion}
                  </button>
                ))}
              </div>
            </div>
          ) : (
            <ol className="assistant-log" aria-label="Conversation" role="log" aria-busy={conversation.streaming}>
              {conversation.messages.map((message, index) => {
                const previous = conversation.messages[index - 1];
                return (
                  <Message
                    key={message.id}
                    message={message}
                    thinking={
                      conversation.streaming &&
                      index === conversation.messages.length - 1 &&
                      message.text === '' &&
                      !message.answer &&
                      !message.suggestions
                    }
                    busy={conversation.streaming}
                    footer={
                      conversation.conversationId !== undefined &&
                      message.role === 'assistant' &&
                      message.turn !== undefined &&
                      !(conversation.streaming && index === conversation.messages.length - 1) ? (
                        <AnswerRatingButtons
                          conversationId={conversation.conversationId}
                          turn={message.turn}
                          rating={message.rating}
                          onRated={(rating) => {
                            if (message.turn !== undefined) conversation.rated(message.turn, rating);
                          }}
                        />
                      ) : null
                    }
                    onPick={(question) => void conversation.ask(question, 'quick')}
                    onAskAi={
                      aiEnabled && message.suggestions?.aiAvailable && previous?.role === 'user'
                        ? () => void conversation.ask(previous.text, 'ai')
                        : undefined
                    }
                  />
                );
              })}
            </ol>
          )}

          {conversation.error && <AssistantError error={conversation.error} />}
        </div>
      </div>

      <Composer
        value={draft}
        onChange={onDraftChange}
        onSend={send}
        onStop={conversation.stop}
        streaming={conversation.streaming}
        placeholder={conversation.messages.length === 0 ? 'Ask about your orders, the catalogue…' : 'Reply to the assistant…'}
      />
    </section>
  );
}

interface MessageProps {
  readonly message: AssistantMessage;
  /** Still streaming with no words yet: the model can think for a minute before its first one. */
  readonly thinking: boolean;
  /** An answer is in flight: the suggestion buttons wait. */
  readonly busy: boolean;
  readonly onPick: (question: string) => void;
  readonly onAskAi?: (() => void) | undefined;
  /** Under an answer: its rating buttons, once the answer is saved. */
  readonly footer?: React.ReactNode;
}

function Message({ message, thinking, busy, onPick, onAskAi, footer }: MessageProps): React.JSX.Element {
  if (message.role === 'user') {
    return (
      <li className="assistant-message assistant-message--user">
        <span className="visually-hidden">You: </span>
        <p className="assistant-message__bubble">{message.text}</p>
      </li>
    );
  }

  if (message.answer) {
    return (
      <li className="assistant-message assistant-message--assistant">
        <span className="visually-hidden">Assistant: </span>
        <QuickAnswerCard answer={message.answer} />
        {footer}
      </li>
    );
  }

  if (message.suggestions) {
    return (
      <li className="assistant-message assistant-message--assistant">
        <span className="visually-hidden">Assistant: </span>
        <SuggestionsReply suggestions={message.suggestions} onPick={onPick} onAskAi={onAskAi} busy={busy} />
        {footer}
      </li>
    );
  }

  return (
    <li className="assistant-message assistant-message--assistant">
      <span className="visually-hidden">Assistant: </span>
      {message.source === 'ai' && message.text !== '' && <p className="assistant-badge assistant-badge--ai">AI answer</p>}
      {message.tools.length > 0 && (
        <ul className="assistant-tools" aria-label="Tools used">
          {message.tools.map((tool, index) => (
            // A tool can be used twice in one answer, so its position is part of its key; the list
            // only ever grows at its end, so positions never shift.
            <li key={`${tool}-${String(index)}`}>Checked {toolLabels[tool] ?? tool}</li>
          ))}
        </ul>
      )}
      {thinking && (
        <p className="muted" role="status">
          Thinking…
        </p>
      )}
      {message.text !== '' && <AssistantMarkdown text={message.text} />}
      {message.stopped && <p className="muted">Stopped.</p>}
      {message.outcome === 'Capped' && (
        <p className="muted">The assistant reached its step limit and answered with what it had.</p>
      )}
      {message.outcome === 'Truncated' && <p className="muted">This answer was cut short.</p>}
      {footer}
    </li>
  );
}

interface AssistantErrorProps {
  readonly error: Error;
}

function AssistantError({ error }: AssistantErrorProps): React.JSX.Element {
  if (error instanceof ApiError && error.status === 429) {
    return <p role="alert">You've asked a lot in the last hour — try again later.</p>;
  }

  return <ErrorPanel error={error} />;
}
