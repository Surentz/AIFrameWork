import { useCallback, useEffect, useReducer, useRef } from 'react';
import { askAssistant, askQuick } from '../../api/assistant';
import { conversationReducer, initialConversation } from './conversation';
import type { ConversationState } from './conversation';
import type { AskMode } from './types';

/** How one question ended. Only `answered` was saved on the server. */
export type AskOutcome = 'answered' | 'failed' | 'stopped';

export interface ConversationOptions {
  /** Where the chat starts: a saved conversation (fromSaved), or empty. Read once. */
  readonly initial?: ConversationState | undefined;
  /** After each saved answer, with the conversation's id: a new chat learns its id here. */
  readonly onSaved?: ((conversationId: string) => void) | undefined;
}

export interface AssistantConversation extends ConversationState {
  readonly ask: (question: string, mode?: AskMode) => Promise<AskOutcome>;
  readonly stop: () => void;
}

/**
 * One conversation with the assistant. A stream is not a cacheable query, so this is client state
 * in a reducer rather than TanStack Query. An answer in flight is aborted on Stop, on New
 * chat, and when the page unmounts — the server then saves nothing for that question.
 */
export function useAssistantConversation(options: ConversationOptions = {}): AssistantConversation {
  const [state, dispatch] = useReducer(conversationReducer, options.initial ?? initialConversation);
  const { onSaved } = options;
  const inFlight = useRef<AbortController | null>(null);

  const ask = useCallback(
    (question: string, mode: AskMode = 'ai'): Promise<AskOutcome> => {
      const controller = new AbortController();
      inFlight.current = controller;
      dispatch({ type: 'asked', question, mode });

      let outcome: AskOutcome = 'answered';
      return (mode === 'quick' ? askQuick : askAssistant)(
        { conversationId: state.conversationId ?? null, question },
        {
          signal: controller.signal,
          onEvent: (event) => {
            if (event.type === 'error') {
              outcome = 'failed';
            }
            dispatch({ type: 'event', event });
            if (event.type === 'done' && typeof event.conversationId === 'string') {
              onSaved?.(event.conversationId);
            }
          },
        },
      ).then(
        () => outcome,
        (error: unknown): AskOutcome => {
          if (controller.signal.aborted) {
            dispatch({ type: 'stopped' });
            return 'stopped';
          }

          dispatch({
            type: 'failed',
            error: error instanceof Error ? error : new Error(String(error)),
          });
          return 'failed';
        },
      );
    },
    [state.conversationId, onSaved],
  );

  const stop = useCallback(() => {
    inFlight.current?.abort();
  }, []);

  // Not a fetch: the cleanup that aborts an answer still streaming when the page goes away.
  useEffect(
    () => () => {
      inFlight.current?.abort();
    },
    [],
  );

  return { ...state, ask, stop };
}
