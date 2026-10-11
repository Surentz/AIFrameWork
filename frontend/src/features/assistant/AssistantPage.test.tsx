import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { server } from '../../test/handlers';
import { sseEvent, sseResponse } from '../../test/sse';
import { withQueryClient } from '../../test/withQueryClient';
import { AssistantPage } from './AssistantPage';

function Address(): React.JSX.Element {
  return <p data-testid="address">{useLocation().pathname}</p>;
}

function renderAt(path: string, enabled = true): void {
  server.use(http.get('/api/assistant', () => HttpResponse.json({ enabled })));
  const page = (
    <>
      <AssistantPage />
      <Address />
    </>
  );
  render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/assistant" element={page} />
        <Route path="/assistant/:conversationId" element={page} />
      </Routes>
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

async function ask(question: string): Promise<void> {
  const user = userEvent.setup();
  await user.type(await screen.findByRole('textbox', { name: 'Your question' }), question);
  await user.click(screen.getByRole('button', { name: 'Send' }));
}

describe('AssistantPage', () => {
  it('says the assistant is switched off when it is', async () => {
    renderAt('/assistant', false);

    expect(await screen.findByText(/The assistant is switched off/)).toBeInTheDocument();
    expect(screen.queryByRole('navigation', { name: 'Conversations' })).not.toBeInTheDocument();
  });

  it('opens a saved conversation with its messages', async () => {
    server.use(
      http.get('/api/assistant/conversations/c-7', () =>
        HttpResponse.json({
          id: 'c-7',
          title: 'Where is my order?',
          messages: [
            { role: 'user', text: 'Where is my order?', tools: [] },
            { role: 'assistant', text: 'It **shipped**.', tools: ['my_orders'] },
          ],
        }),
      ),
    );
    renderAt('/assistant/c-7');

    expect(await screen.findByText('shipped')).toBeInTheDocument();
    expect(screen.getByRole('list', { name: 'Tools used' })).toHaveTextContent('Checked your orders');
  });

  it('continues a saved conversation with its id', async () => {
    let body: unknown;
    server.use(
      http.get('/api/assistant/conversations/c-7', () =>
        HttpResponse.json({ id: 'c-7', title: 'T', messages: [{ role: 'user', text: 'Q', tools: [] }, { role: 'assistant', text: 'A', tools: [] }] }),
      ),
      http.post('/api/assistant/messages', async ({ request }) => {
        body = await request.json();
        return sseResponse(sseEvent({ type: 'text', delta: 'More.' }), sseEvent({ type: 'done', conversationId: 'c-7', outcome: 'Answered' }));
      }),
    );
    renderAt('/assistant/c-7');
    await screen.findByText('A');

    await ask('And?');

    expect(await screen.findByText('More.')).toBeInTheDocument();
    expect(body).toEqual({ conversationId: 'c-7', question: 'And?' });
  });

  it('gives a new chat its address after the first answer, without restarting it', async () => {
    const bodies: unknown[] = [];
    server.use(
      http.post('/api/assistant/messages', async ({ request }) => {
        bodies.push(await request.json());
        return sseResponse(
          sseEvent({ type: 'text', delta: bodies.length === 1 ? 'First.' : 'Second.' }),
          sseEvent({ type: 'done', conversationId: 'c-new', outcome: 'Answered' }),
        );
      }),
    );
    renderAt('/assistant');

    await ask('One?');
    expect(await screen.findByText('First.')).toBeInTheDocument();
    await waitFor(() => {
      expect(screen.getByTestId('address')).toHaveTextContent('/assistant/c-new');
    });
    await ask('Two?');

    expect(await screen.findByText('Second.')).toBeInTheDocument();
    expect(screen.getByText('First.')).toBeInTheDocument();
    expect(bodies[1]).toEqual({ conversationId: 'c-new', question: 'Two?' });
  });

  it('refreshes the sidebar when an answer is saved', async () => {
    let listed = 0;
    server.use(
      http.get('/api/assistant/conversations', () => {
        listed += 1;
        return HttpResponse.json(listed === 1 ? [] : [{ id: 'c-new', title: 'One?', lastActiveAt: new Date().toISOString() }]);
      }),
      http.post('/api/assistant/messages', () =>
        sseResponse(sseEvent({ type: 'text', delta: 'Done.' }), sseEvent({ type: 'done', conversationId: 'c-new', outcome: 'Answered' })),
      ),
    );
    renderAt('/assistant');

    await ask('One?');

    expect(await screen.findByRole('link', { name: 'One?' })).toBeInTheDocument();
  });

  it('starts an empty chat on New chat, keeping a draft', async () => {
    server.use(
      http.post('/api/assistant/messages', () =>
        sseResponse(sseEvent({ type: 'text', delta: 'Old answer.' }), sseEvent({ type: 'done', conversationId: 'c-1', outcome: 'Answered' })),
      ),
    );
    renderAt('/assistant');
    const user = userEvent.setup();
    await ask('Old?');
    await screen.findByText('Old answer.');
    await user.type(screen.getByRole('textbox', { name: 'Your question' }), 'Keep me');

    await user.click(screen.getByRole('button', { name: 'New chat' }));

    expect(await screen.findByRole('heading', { name: 'How can I help you today?' })).toBeInTheDocument();
    expect(screen.queryByText('Old answer.')).not.toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'Your question' })).toHaveValue('Keep me');
    expect(screen.getByTestId('address')).toHaveTextContent(/^\/assistant$/);
  });

  it('opens another conversation from the sidebar as a fresh chat', async () => {
    server.use(
      http.get('/api/assistant/conversations', () =>
        HttpResponse.json([{ id: 'c-2', title: 'Other one', lastActiveAt: new Date().toISOString() }]),
      ),
      http.get('/api/assistant/conversations/c-2', () =>
        HttpResponse.json({ id: 'c-2', title: 'Other one', messages: [{ role: 'user', text: 'Other question', tools: [] }] }),
      ),
      http.post('/api/assistant/messages', () =>
        sseResponse(sseEvent({ type: 'text', delta: 'First chat.' }), sseEvent({ type: 'done', conversationId: 'c-1', outcome: 'Answered' })),
      ),
    );
    renderAt('/assistant');
    await ask('First?');
    await screen.findByText('First chat.');

    await userEvent.click(screen.getByRole('link', { name: 'Other one' }));

    expect(await screen.findByText('Other question')).toBeInTheDocument();
    expect(screen.queryByText('First chat.')).not.toBeInTheDocument();
  });

  it('puts the failed question back, still there after New chat', async () => {
    server.use(
      http.post('/api/assistant/messages', () =>
        HttpResponse.json(
          { status: 404, title: 'assistant.conversation_not_found', detail: 'That conversation does not exist or has expired. Start a new one.' },
          { status: 404 },
        ),
      ),
    );
    renderAt('/assistant');

    await ask('Two?');
    await screen.findByText(/has expired/);
    await userEvent.click(screen.getByRole('button', { name: 'New chat' }));

    expect(await screen.findByRole('textbox', { name: 'Your question' })).toHaveValue('Two?');
  });

  it('says so when a saved conversation is gone', async () => {
    server.use(
      http.get('/api/assistant/conversations/gone', () =>
        HttpResponse.json({ status: 404, title: 'assistant.conversation_not_found', detail: 'That conversation does not exist or has expired.' }, { status: 404 }),
      ),
    );
    renderAt('/assistant/gone');

    expect(await screen.findByText(/does not exist or has expired/)).toBeInTheDocument();
  });
});
