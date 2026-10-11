import { act, renderHook, waitFor } from '@testing-library/react';
import { http } from 'msw';
import { server } from '../../test/handlers';
import { openSseResponse, sseEvent } from '../../test/sse';
import { useAssistantConversation } from './useAssistantConversation';

describe('useAssistantConversation', () => {
  it('aborts the answer in flight when the page goes away', async () => {
    // MSW does not pass a client abort on to the handler's request.signal once it has answered, so
    // the test watches the signal the request was sent with. The spy calls through: MSW still
    // answers, and the API client is not mocked.
    const fetchSpy = vi.spyOn(globalThis, 'fetch');
    server.use(
      http.post('/api/assistant/messages', () =>
        openSseResponse(sseEvent({ type: 'text', delta: 'Still writing' })),
      ),
    );
    const { result, unmount } = renderHook(() => useAssistantConversation());

    act(() => {
      void result.current.ask('Why?');
    });
    await waitFor(() => {
      expect(result.current.messages.at(-1)?.text).toBe('Still writing');
    });
    unmount();

    expect(fetchSpy.mock.calls[0]?.[1]?.signal?.aborted).toBe(true);
    fetchSpy.mockRestore();
  });
});
