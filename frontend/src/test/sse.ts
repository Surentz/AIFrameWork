import { HttpResponse } from 'msw';

const encoder = new TextEncoder();

/** One server-sent event, exactly as SseAssistantOutput writes it. */
export function sseEvent(event: { readonly type: string } & Record<string, unknown>): string {
  return `event: ${event.type}\ndata: ${JSON.stringify(event)}\n\n`;
}

/** A text/event-stream response that sends each chunk, then closes. */
export function sseResponse(...chunks: string[]): HttpResponse<ReadableStream> {
  return new HttpResponse(
    new ReadableStream({
      start(controller) {
        for (const chunk of chunks) {
          controller.enqueue(encoder.encode(chunk));
        }
        controller.close();
      },
    }),
    { headers: { 'Content-Type': 'text/event-stream' } },
  );
}

/** The same, but never closes: an answer still being written, for Stop and unmount tests. */
export function openSseResponse(...chunks: string[]): HttpResponse<ReadableStream> {
  return new HttpResponse(
    new ReadableStream({
      start(controller) {
        for (const chunk of chunks) {
          controller.enqueue(encoder.encode(chunk));
        }
      },
    }),
    { headers: { 'Content-Type': 'text/event-stream' } },
  );
}
