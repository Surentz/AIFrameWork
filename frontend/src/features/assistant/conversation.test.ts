import { ApiError } from '../../api/client';
import { conversationReducer, initialConversation, fromSaved } from './conversation';
import type { ConversationAction, ConversationState } from './conversation';

function run(...actions: ConversationAction[]): ConversationState {
  return actions.reduce(conversationReducer, initialConversation);
}

const asked: ConversationAction = { type: 'asked', question: 'Why?', mode: 'ai' };

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

describe('quick replies', () => {
  const quickAsked = conversationReducer(initialConversation, { type: 'asked', question: 'Has it shipped?', mode: 'quick' });

  it('marks the answer as coming from the quick path', () => {
    expect(quickAsked.messages.at(-1)?.source).toBe('quick');
  });

  it('puts an answer event on the answer', () => {
    const state = conversationReducer(quickAsked, {
      type: 'event',
      event: { type: 'answer', answer: { kind: 'Instant', text: 'Shipped.', facts: [] } },
    });

    expect(state.messages.at(-1)?.answer?.text).toBe('Shipped.');
  });

  it('puts a suggestions event on the answer', () => {
    const state = conversationReducer(quickAsked, {
      type: 'event',
      event: { type: 'suggestions', suggestions: { text: 'Not sure.', options: ['Is my export ready?'], aiAvailable: true } },
    });

    expect(state.messages.at(-1)?.suggestions?.options).toEqual(['Is my export ready?']);
  });

  it('keeps an answer that has a card but no text when the stream ends', () => {
    const answered = conversationReducer(quickAsked, {
      type: 'event',
      event: { type: 'answer', answer: { kind: 'Instant', text: 'Shipped.', facts: [] } },
    });
    const state = conversationReducer(answered, { type: 'event', event: { type: 'done', conversationId: 'c1' } });

    expect(state.messages).toHaveLength(2);
  });

  it('restores a saved quick answer with its card', () => {
    const state = fromSaved({
      id: 'c1',
      title: 'Has it shipped?',
      messages: [
        { role: 'user', text: 'Has it shipped?', tools: [] },
        { role: 'assistant', text: 'Shipped.', tools: [], answer: { kind: 'Instant', text: 'Shipped.', facts: [] } },
      ],
    });

    expect(state.messages[1]?.answer?.text).toBe('Shipped.');
    expect(state.messages[1]?.source).toBe('quick');
  });
});

describe('turns and ratings', () => {
  const quickAsked = conversationReducer(initialConversation, { type: 'asked', question: 'Q?', mode: 'quick' });

  it('takes the turn from the answer event', () => {
    const state = conversationReducer(quickAsked, {
      type: 'event',
      event: { type: 'answer', turn: 1, answer: { kind: 'Instant', text: 'A.', facts: [] } },
    });

    expect(state.messages.at(-1)?.turn).toBe(1);
  });

  it('takes the turn from done for an AI answer', () => {
    const state = conversationReducer(quickAsked, { type: 'event', event: { type: 'done', conversationId: 'c1', turn: 3 } });

    expect(state.messages.at(-1)?.turn).toBe(3);
  });

  it('records a rating on the message with that turn', () => {
    const answered = conversationReducer(quickAsked, { type: 'event', event: { type: 'done', conversationId: 'c1', turn: 1 } });

    const rated = conversationReducer(answered, { type: 'rated', turn: 1, rating: 'Down' });

    expect(rated.messages.at(-1)?.rating).toBe('Down');
  });

  it('starts a saved conversation with its turns and ratings', () => {
    const state = fromSaved({
      id: 'c1',
      title: 'T',
      messages: [
        { role: 'user', text: 'Q?', tools: [] },
        { role: 'assistant', text: 'A.', tools: [], turn: 1, rating: 'Up' },
      ],
    });

    expect(state.messages[1]).toMatchObject({ turn: 1, rating: 'Up' });
  });
});
