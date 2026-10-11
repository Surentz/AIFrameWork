import { SseParser } from './sse';

const text = (delta: string): string => `event: text\ndata: {"type":"text","delta":"${delta}"}\n\n`;

describe('SseParser', () => {
  it('returns one event for one complete block', () => {
    expect(new SseParser().push(text('Hello'))).toEqual([{ type: 'text', delta: 'Hello' }]);
  });

  it('returns every event in a chunk that holds several', () => {
    const events = new SseParser().push(text('a') + text('b'));

    expect(events.map((e) => e.delta)).toEqual(['a', 'b']);
  });

  it('reassembles an event split across chunks mid-line', () => {
    const parser = new SseParser();
    const whole = text('split');

    const first = parser.push(whole.slice(0, 9));
    const second = parser.push(whole.slice(9, 30));
    const third = parser.push(whole.slice(30));

    expect([...first, ...second, ...third]).toEqual([{ type: 'text', delta: 'split' }]);
  });

  it('accepts CRLF line endings, even split between the CR and the LF', () => {
    const parser = new SseParser();
    const crlf = text('crlf').replaceAll('\n', '\r\n');

    const events = [...parser.push(crlf.slice(0, -1)), ...parser.push(crlf.slice(-1))];

    expect(events).toEqual([{ type: 'text', delta: 'crlf' }]);
  });

  it('ignores the keep-alive comment', () => {
    expect(new SseParser().push(`: keep-alive\n\n${text('after')}`)).toEqual([
      { type: 'text', delta: 'after' },
    ]);
  });

  it('keeps an unfinished event until its blank line arrives', () => {
    const parser = new SseParser();

    expect(parser.push('event: text\ndata: {"type":"text","delta":"x"}\n')).toEqual([]);
    expect(parser.push('\n')).toEqual([{ type: 'text', delta: 'x' }]);
  });
});
