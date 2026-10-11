import { http, HttpResponse } from 'msw';
import { server } from '../test/handlers';
import { sseEvent, sseResponse } from '../test/sse';
import type { AssistantEvent } from '../features/assistant/types';
import {
  askAssistant,
  deleteConversation,
  getConversation,
  listConversations,
  renameConversation,
} from './assistant';
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

describe('the conversation history', () => {
  it('lists the conversations', async () => {
    server.use(
      http.get('/api/assistant/conversations', () =>
        HttpResponse.json([{ id: 'c-1', title: 'Why?', lastActiveAt: '2026-10-11T09:00:00Z' }]),
      ),
    );

    expect(await listConversations()).toEqual([
      { id: 'c-1', title: 'Why?', lastActiveAt: '2026-10-11T09:00:00Z' },
    ]);
  });

  it('reads one conversation', async () => {
    server.use(
      http.get('/api/assistant/conversations/c-1', () =>
        HttpResponse.json({ id: 'c-1', title: 'Why?', messages: [] }),
      ),
    );

    expect((await getConversation('c-1')).title).toBe('Why?');
  });

  it('renames with a PATCH carrying the title', async () => {
    let body: unknown;
    server.use(
      http.patch('/api/assistant/conversations/c-1', async ({ request }) => {
        body = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );

    await renameConversation('c-1', 'Orders');

    expect(body).toEqual({ title: 'Orders' });
  });

  it('deletes with a DELETE', async () => {
    let deleted = false;
    server.use(
      http.delete('/api/assistant/conversations/c-1', () => {
        deleted = true;
        return new HttpResponse(null, { status: 204 });
      }),
    );

    await deleteConversation('c-1');

    expect(deleted).toBe(true);
  });
});
