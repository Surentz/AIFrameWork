import { ApiError } from '../../api/client';
import { conversationReducer, initialConversation, fromSaved } from './conversation';
import type { ConversationAction, ConversationState } from './conversation';

function run(...actions: ConversationAction[]): ConversationState {
  return actions.reduce(conversationReducer, initialConversation);
}

const asked: ConversationAction = { type: 'asked', question: 'Why?' };

describe('conversationReducer', () => {
  it('adds the question and an empty answer, and starts streaming', () => {
    const state = run(asked);

    expect(state.messages.map((m) => [m.role, m.text])).toEqual([
      ['user', 'Why?'],
      ['assistant', ''],
    ]);
    expect(state.streaming).toBe(true);
  });

  it('appends each text delta to the answer', () => {
    const state = run(
      asked,
      { type: 'event', event: { type: 'text', delta: 'All ' } },
      { type: 'event', event: { type: 'text', delta: 'fine.' } },
    );

    expect(state.messages.at(-1)?.text).toBe('All fine.');
  });

  it('records each tool the answer used', () => {
    const state = run(asked, { type: 'event', event: { type: 'tool', tool: 'job_health' } });

    expect(state.messages.at(-1)?.tools).toEqual(['job_health']);
  });

  it('keeps the conversation id and outcome when the answer is done', () => {
    const state = run(asked, {
      type: 'event',
      event: { type: 'done', conversationId: 'c-1', outcome: 'Capped' },
    });

    expect(state.conversationId).toBe('c-1');
    expect(state.messages.at(-1)?.outcome).toBe('Capped');
    expect(state.streaming).toBe(false);
  });

  it('turns an error event into an ApiError with its status and title', () => {
    const state = run(asked, {
      type: 'event',
      event: {
        type: 'error',
        problem: { status: 503, title: 'assistant.unavailable', detail: 'Down.' },
      },
    });

    expect(state.error).toBeInstanceOf(ApiError);
    expect(state.error).toMatchObject({
      status: 503,
      title: 'assistant.unavailable',
      message: 'Down.',
    });
    expect(state.streaming).toBe(false);
  });

  it('drops the empty answer when the question fails before any text', () => {
    const state = run(asked, { type: 'failed', error: new Error('nope') });

    expect(state.messages.map((m) => m.role)).toEqual(['user']);
    expect(state.error?.message).toBe('nope');
  });

  it('marks the answer stopped', () => {
    const state = run(
      asked,
      { type: 'event', event: { type: 'text', delta: 'Partly' } },
      { type: 'stopped' },
    );

    expect(state.messages.at(-1)?.stopped).toBe(true);
    expect(state.streaming).toBe(false);
  });

  it('forgets everything on reset, the conversation id included', () => {
    const state = run(
      asked,
      { type: 'event', event: { type: 'done', conversationId: 'c-1', outcome: 'Answered' } },
      { type: 'reset' },
    );

    expect(state).toEqual(initialConversation);
  });

  it('ignores a stop that arrives after a reset', () => {
    expect(run({ type: 'reset' }, { type: 'stopped' })).toEqual(initialConversation);
  });
});

describe('fromSaved', () => {
  it('starts from the saved messages and id, not streaming', () => {
    const state = fromSaved({
      id: 'c-1',
      title: 'Why?',
      messages: [
        { role: 'user', text: 'Why?', tools: [] },
        { role: 'assistant', text: 'Because.', tools: ['my_orders'] },
      ],
    });

    expect(state.conversationId).toBe('c-1');
    expect(state.streaming).toBe(false);
    expect(state.messages.map((m) => [m.id, m.role, m.text, m.tools])).toEqual([
      [0, 'user', 'Why?', []],
      [1, 'assistant', 'Because.', ['my_orders']],
    ]);
  });
});
