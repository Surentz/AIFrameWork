import type { AssistantEvent } from './types';

/**
 * Turns a text/event-stream body into the assistant's events. A network chunk can end anywhere —
 * mid-line, mid-JSON, between a CR and its LF — so whatever has not yet ended in a blank line is
 * kept for the next push. Comment lines (": keep-alive") are the server's heartbeat, not events.
 */
export class SseParser {
  private buffer = '';

  push(chunk: string): AssistantEvent[] {
    // A lone trailing CR may be half of a CRLF; it is normalised once its LF arrives.
    this.buffer = (this.buffer + chunk).replaceAll('\r\n', '\n');

    const events: AssistantEvent[] = [];
    for (let end = this.buffer.indexOf('\n\n'); end !== -1; end = this.buffer.indexOf('\n\n')) {
      const block = this.buffer.slice(0, end);
      this.buffer = this.buffer.slice(end + 2);

      const event = parseBlock(block);
      if (event !== undefined) {
        events.push(event);
      }
    }

    return events;
  }
}

function parseBlock(block: string): AssistantEvent | undefined {
  const data = block
    .split('\n')
    .filter((line) => line.startsWith('data:'))
    .map((line) => line.slice('data:'.length).trimStart())
    .join('\n');

  // A block of comments only (the keep-alive) carries no data.
  return data === '' ? undefined : (JSON.parse(data) as AssistantEvent);
}
