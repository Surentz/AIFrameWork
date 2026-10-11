import { useCallback, useEffect, useReducer, useRef } from 'react';
import { askAssistant } from '../../api/assistant';
import { conversationReducer, initialConversation } from './conversation';
import type { ConversationState } from './conversation';

export interface AssistantConversation extends ConversationState {
  readonly ask: (question: string) => void;
  readonly stop: () => void;
  readonly startOver: () => void;
}

/**
 * One conversation with the assistant. A stream is not a cacheable query, so this is client state
 * in a reducer rather than TanStack Query. An answer in flight is aborted on Stop, on New
 * conversation, and when the page unmounts — the server then saves nothing for that question.
 */
export function useAssistantConversation(): AssistantConversation {
  const [state, dispatch] = useReducer(conversationReducer, initialConversation);
  const inFlight = useRef<AbortController | null>(null);

  const ask = useCallback(
    (question: string) => {
      const controller = new AbortController();
      inFlight.current = controller;
      dispatch({ type: 'asked', question });

      void askAssistant(
        { conversationId: state.conversationId ?? null, question },
        {
          signal: controller.signal,
          onEvent: (event) => {
            dispatch({ type: 'event', event });
          },
        },
      ).catch((error: unknown) => {
        if (controller.signal.aborted) {
          dispatch({ type: 'stopped' });
          return;
        }

        dispatch({
          type: 'failed',
          error: error instanceof Error ? error : new Error(String(error)),
        });
      });
    },
    [state.conversationId],
  );

  const stop = useCallback(() => {
    inFlight.current?.abort();
  }, []);

  const startOver = useCallback(() => {
    inFlight.current?.abort();
    dispatch({ type: 'reset' });
  }, []);

  // Not a fetch: the cleanup that aborts an answer still streaming when the page goes away.
  useEffect(
    () => () => {
      inFlight.current?.abort();
    },
    [],
  );

  return { ...state, ask, stop, startOver };
}
