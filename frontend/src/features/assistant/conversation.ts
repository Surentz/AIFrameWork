import { ApiError } from '../../api/client';
import type {
  AnswerRating,
  AskMode,
  AssistantAnswer,
  AssistantEvent,
  AssistantOutcome,
  AssistantSuggestions,
  SavedConversation,
} from './types';

export interface AssistantMessage {
  readonly id: number;
  readonly role: 'user' | 'assistant';
  readonly text: string;
  readonly tools: readonly string[];
  readonly outcome?: AssistantOutcome | undefined;
  readonly stopped?: boolean | undefined;
  /** An instant or help answer's card. */
  readonly answer?: AssistantAnswer | undefined;
  /** A not-sure reply's suggestions. */
  readonly suggestions?: AssistantSuggestions | undefined;
  /** Which path wrote this answer. */
  readonly source?: AskMode | undefined;
  /** The stored turn that carries this answer: what a rating names. Unknown until the answer's turn arrives. */
  readonly turn?: number | undefined;
  readonly rating?: AnswerRating | undefined;
}

export interface ConversationState {
  readonly conversationId: string | undefined;
  readonly messages: readonly AssistantMessage[];
  readonly streaming: boolean;
  readonly error: Error | undefined;
}

export type ConversationAction =
  | { readonly type: 'asked'; readonly question: string; readonly mode: AskMode }
  | { readonly type: 'event'; readonly event: AssistantEvent }
  | { readonly type: 'failed'; readonly error: Error }
  | { readonly type: 'rated'; readonly turn: number; readonly rating: AnswerRating | undefined }
  | { readonly type: 'stopped' }
  | { readonly type: 'reset' };

export const initialConversation: ConversationState = {
  conversationId: undefined,
  messages: [],
  streaming: false,
  error: undefined,
};

/**
 * The conversation as the page shows it. Pure, so every transition is a plain unit test. The
 * conversation id is the server's (from `done`); a stopped or failed question saved nothing there,
 * so the id is kept and the next question continues from the last saved answer.
 */
export function conversationReducer(
  state: ConversationState,
  action: ConversationAction,
): ConversationState {
  switch (action.type) {
    case 'asked':
      return {
        ...state,
        streaming: true,
        error: undefined,
        messages: [
          ...state.messages,
          { id: state.messages.length, role: 'user', text: action.question, tools: [] },
          { id: state.messages.length + 1, role: 'assistant', text: '', tools: [], source: action.mode },
        ],
      };
    case 'event':
      return applyEvent(state, action.event);
    case 'failed':
      return { ...dropEmptyAnswer(state), streaming: false, error: action.error };
    case 'stopped':
      return state.messages.length === 0
        ? state
        : { ...updateAnswer(state, (m) => ({ ...m, stopped: true })), streaming: false };
    case 'rated':
      return {
        ...state,
        messages: state.messages.map((m) => (m.role === 'assistant' && m.turn === action.turn ? { ...m, rating: action.rating } : m)),
      };
    case 'reset':
      return initialConversation;
  }
}

function applyEvent(state: ConversationState, event: AssistantEvent): ConversationState {
  switch (event.type) {
    case 'text':
      return updateAnswer(state, (m) => ({ ...m, text: m.text + (event.delta ?? '') }));
    case 'tool':
      return updateAnswer(state, (m) => ({ ...m, tools: [...m.tools, event.tool ?? 'a tool'] }));
    case 'done':
      return {
        ...updateAnswer(state, (m) => ({ ...m, outcome: event.outcome ?? undefined, turn: toTurn(event.turn) ?? m.turn })),
        conversationId: event.conversationId ?? state.conversationId,
        streaming: false,
      };
    case 'error':
      return { ...dropEmptyAnswer(state), streaming: false, error: toApiError(event.problem) };
    case 'answer':
      return updateAnswer(state, (m) => ({ ...m, answer: event.answer ?? undefined, turn: toTurn(event.turn) }));
    case 'suggestions':
      return updateAnswer(state, (m) => ({ ...m, suggestions: event.suggestions ?? undefined, turn: toTurn(event.turn) }));
    default:
      return state;
  }
}

function updateAnswer(
  state: ConversationState,
  change: (message: AssistantMessage) => AssistantMessage,
): ConversationState {
  const last = state.messages.at(-1);
  if (last?.role !== 'assistant') {
    return state;
  }

  return { ...state, messages: [...state.messages.slice(0, -1), change(last)] };
}

/** An answer with no text and no tools says nothing; the error shown in its place does. */
function dropEmptyAnswer(state: ConversationState): ConversationState {
  const last = state.messages.at(-1);
  return last?.role === 'assistant' && last.text === '' && last.tools.length === 0 && last.answer === undefined && last.suggestions === undefined
    ? { ...state, messages: state.messages.slice(0, -1) }
    : state;
}

/** The generated type allows a string (the API accepts numbers as text); the server only ever sends a number. */
function toTurn(turn: number | string | null | undefined): number | undefined {
  const value = turn === null || turn === undefined ? Number.NaN : Number(turn);
  return Number.isInteger(value) ? value : undefined;
}

function toApiError(problem: AssistantEvent['problem']): ApiError {
  // traceId is a ProblemDetails extension, so the generated type does not name it.
  const extensions = (problem ?? {}) as Readonly<Record<string, unknown>>;
  const traceId = typeof extensions.traceId === 'string' ? extensions.traceId : undefined;

  return new ApiError(Number(problem?.status ?? 500), {
    ...(problem?.title ? { title: problem.title } : {}),
    ...(problem?.detail ? { detail: problem.detail } : {}),
    ...(traceId ? { traceId } : {}),
  });
}

/** A saved conversation as the reducer starts from it: its messages, its id, nothing streaming. */
export function fromSaved(saved: SavedConversation): ConversationState {
  return {
    conversationId: saved.id,
    streaming: false,
    error: undefined,
    messages: saved.messages.map((message, index) => ({
      id: index,
      role: message.role === 'user' ? 'user' : 'assistant',
      text: message.text,
      tools: message.tools,
      answer: message.answer ?? undefined,
      suggestions: message.suggestions ?? undefined,
      source: message.answer || message.suggestions ? 'quick' : 'ai',
      turn: toTurn(message.turn),
      rating: message.rating ?? undefined,
    })),
  };
}
