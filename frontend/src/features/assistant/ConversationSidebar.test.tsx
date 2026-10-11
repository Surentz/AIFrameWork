import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { ConversationSidebar } from './ConversationSidebar';

const today = new Date().toISOString();

function Address(): React.JSX.Element {
  return <p data-testid="address">{useLocation().pathname}</p>;
}

function renderSidebar(activeId?: string): { onNewChat: () => void } {
  const onNewChat = vi.fn();
  render(
    <MemoryRouter initialEntries={[activeId ? `/assistant/${activeId}` : '/assistant']}>
      <Routes>
        <Route
          path="/assistant/*"
          element={
            <>
              <ConversationSidebar activeId={activeId} onNewChat={onNewChat} />
              <Address />
            </>
          }
        />
      </Routes>
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
  return { onNewChat };
}

function listOnce(...items: { id: string; title: string }[]): void {
  server.use(
    http.get('/api/assistant/conversations', () =>
      HttpResponse.json(items.map((i) => ({ ...i, lastActiveAt: today }))),
    ),
  );
}

describe('ConversationSidebar', () => {
  it('lists conversations under their age group, each linking to it', async () => {
    listOnce({ id: 'c-1', title: 'Where is my order?' });
    renderSidebar();

    const nav = await screen.findByRole('navigation', { name: 'Conversations' });
    expect(await within(nav).findByRole('heading', { name: 'Today' })).toBeInTheDocument();
    expect(within(nav).getByRole('link', { name: 'Where is my order?' })).toHaveAttribute(
      'href',
      '/assistant/c-1',
    );
  });

  it('marks the open conversation as current', async () => {
    listOnce({ id: 'c-1', title: 'Open one' });
    renderSidebar('c-1');

    expect(await screen.findByRole('link', { name: 'Open one' })).toHaveAttribute(
      'aria-current',
      'page',
    );
  });

  it('asks for a new chat', async () => {
    const { onNewChat } = renderSidebar();

    await userEvent.click(await screen.findByRole('button', { name: 'New chat' }));

    expect(onNewChat).toHaveBeenCalledOnce();
  });

  it('renames a conversation and shows the new title', async () => {
    let titles = ['Old title'];
    server.use(
      http.get('/api/assistant/conversations', () =>
        HttpResponse.json([{ id: 'c-1', title: titles[0], lastActiveAt: today }]),
      ),
      http.patch('/api/assistant/conversations/c-1', async ({ request }) => {
        titles = [((await request.json()) as { title: string }).title];
        return new HttpResponse(null, { status: 204 });
      }),
    );
    renderSidebar();
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Options for Old title' }));
    await user.click(screen.getByRole('button', { name: 'Rename' }));
    const input = screen.getByRole('textbox', { name: 'Conversation title' });
    await user.clear(input);
    await user.type(input, 'New title{Enter}');

    expect(await screen.findByRole('link', { name: 'New title' })).toBeInTheDocument();
  });

  it('cancels a rename with Escape', async () => {
    listOnce({ id: 'c-1', title: 'Keep me' });
    renderSidebar();
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Options for Keep me' }));
    await user.click(screen.getByRole('button', { name: 'Rename' }));
    await user.keyboard('{Escape}');

    expect(screen.getByRole('link', { name: 'Keep me' })).toBeInTheDocument();
  });

  it('deletes the open conversation after confirming, and goes to a new chat', async () => {
    let items = [{ id: 'c-1', title: 'Doomed', lastActiveAt: today }];
    server.use(
      http.get('/api/assistant/conversations', () => HttpResponse.json(items)),
      http.delete('/api/assistant/conversations/c-1', () => {
        items = [];
        return new HttpResponse(null, { status: 204 });
      }),
    );
    renderSidebar('c-1');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Options for Doomed' }));
    await user.click(screen.getByRole('button', { name: 'Delete' }));
    await user.click(screen.getByRole('button', { name: 'Delete conversation' }));

    expect(await screen.findByTestId('address')).toHaveTextContent(/^\/assistant$/);
    expect(screen.queryByRole('link', { name: 'Doomed' })).not.toBeInTheDocument();
  });

  it('shows the error when the list cannot load', async () => {
    server.use(
      http.get('/api/assistant/conversations', () =>
        HttpResponse.json({ status: 500, title: 'error', detail: 'Boom.' }, { status: 500 }),
      ),
    );
    renderSidebar();

    expect(await screen.findByText(/Boom/)).toBeInTheDocument();
  });
});
