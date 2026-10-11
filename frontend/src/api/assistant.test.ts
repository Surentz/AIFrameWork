import { http, HttpResponse } from 'msw';
import { server } from '../test/handlers';
import { sseEvent, sseResponse } from '../test/sse';
import type { AssistantEvent } from '../features/assistant/types';
import { askAssistant } from './assistant';
import { ApiError } from './client';

async function ask(): Promise<AssistantEvent[]> {
  const events: AssistantEvent[] = [];
  await askAssistant(
    { question: 'Why?' },
    { signal: new AbortController().signal, onEvent: (e) => events.push(e) },
  );
  return events;
}

describe('askAssistant', () => {
  it('delivers each event in order as the stream arrives', async () => {
    server.use(
      http.post('/api/assistant/messages', () =>
        sseResponse(
          sseEvent({ type: 'tool', tool: 'job_health' }),
          ': keep-alive\n\n',
          sseEvent({ type: 'text', delta: 'All ' }),
          sseEvent({ type: 'text', delta: 'fine.' }),
          sseEvent({ type: 'done', conversationId: 'c-1', outcome: 'Answered' }),
        ),
      ),
    );

    const events = await ask();

    expect(events.map((e) => e.type)).toEqual(['tool', 'text', 'text', 'done']);
  });

  it('posts the question and conversation id as JSON', async () => {
    let body: unknown;
    server.use(
      http.post('/api/assistant/messages', async ({ request }) => {
        body = await request.json();
        return sseResponse(sseEvent({ type: 'done', conversationId: 'c-2', outcome: 'Answered' }));
      }),
    );

    await askAssistant(
      { conversationId: 'c-1', question: 'And now?' },
      { signal: new AbortController().signal, onEvent: () => undefined },
    );

    expect(body).toEqual({ conversationId: 'c-1', question: 'And now?' });
  });

  it('rejects with the ApiError of a failure before the stream starts', async () => {
    server.use(
      http.post('/api/assistant/messages', () =>
        HttpResponse.json({ status: 429, title: 'Too Many Requests' }, { status: 429 }),
      ),
    );

    await expect(ask()).rejects.toMatchObject({ status: 429 });
    await expect(ask()).rejects.toBeInstanceOf(ApiError);
  });

  it('rejects when the stream ends without done or error', async () => {
    server.use(
      http.post('/api/assistant/messages', () =>
        sseResponse(sseEvent({ type: 'text', delta: 'cut' })),
      ),
    );

    await expect(ask()).rejects.toThrow('stopped before it finished');
  });
});
