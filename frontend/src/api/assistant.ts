import { SseParser } from '../features/assistant/sse';
import type {
  AskAssistantRequest,
  AssistantEvent,
  AssistantStatus,
} from '../features/assistant/types';
import { request, requestStream } from './client';

export function getAssistantStatus(): Promise<AssistantStatus> {
  return request<AssistantStatus>('/api/assistant');
}

export interface AskOptions {
  readonly signal: AbortSignal;
  readonly onEvent: (event: AssistantEvent) => void;
}

/**
 * Asks one question and hands each event to `onEvent` as it arrives. Resolves after `done` or
 * `error`. Rejects with an ApiError for a failure before the stream started (400, 404, 409, 429,
 * 503), with the abort when `signal` fires, and with an Error if the stream ends without either.
 */
export async function askAssistant(body: AskAssistantRequest, options: AskOptions): Promise<void> {
  const stream = await requestStream('/api/assistant/messages', {
    method: 'POST',
    body: JSON.stringify(body),
    signal: options.signal,
  });

  const reader = stream.getReader();
  const decoder = new TextDecoder();
  const parser = new SseParser();
  let finished = false;

  for (let read = await reader.read(); !read.done; read = await reader.read()) {
    for (const event of parser.push(decoder.decode(read.value, { stream: true }))) {
      options.onEvent(event);
      finished ||= event.type === 'done' || event.type === 'error';
    }
  }

  if (!finished) {
    throw new Error('The answer stopped before it finished. Try again.');
  }
}
