import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { anAdminSession, server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { UsersPage } from './UsersPage';

function renderPage(): void {
  render(
    <MemoryRouter>
      <UsersPage />
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

/** The row for one account, so an assertion cannot match a different user's button. */
async function rowFor(username: string): Promise<HTMLElement> {
  const cell = await screen.findByRole('cell', { name: username });
  // eslint-disable-next-line @typescript-eslint/no-non-null-assertion -- a cell is always in a row
  return cell.closest('tr')!;
}

describe('UsersPage', () => {
  it('lists accounts with their role and when they were last seen', async () => {
    renderPage();

    const ada = within(await rowFor('ada'));
    expect(ada.getByText('Member')).toBeInTheDocument();

    const grace = within(await rowFor('grace'));
    expect(grace.getByText('Admin')).toBeInTheDocument();
    // An account that has never made an authenticated request reads as Never, not as a blank.
    expect(grace.getByText('Never')).toBeInTheDocument();
  });

  it('marks an account whose role comes from configuration', async () => {
    renderPage();

    const grace = within(await rowFor('grace'));
    expect(grace.getByText('configured')).toBeInTheDocument();

    const ada = within(await rowFor('ada'));
    expect(ada.queryByText('configured')).not.toBeInTheDocument();
  });

  it('asks for confirmation naming the account before promoting', async () => {
    const user = userEvent.setup();
    renderPage();

    const ada = within(await rowFor('ada'));
    await user.click(ada.getByRole('button', { name: 'Promote' }));

    // Named in words. A misclick in a table row must not be able to act on the wrong person.
    expect(await screen.findByText('Promote ada to Admin?')).toBeInTheDocument();
  });

  it('does not call the API until the confirmation is accepted', async () => {
    const user = userEvent.setup();
    let calls = 0;
    server.use(
      http.post('/api/monitoring/users/:id/role', () => {
        calls += 1;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    renderPage();

    const ada = within(await rowFor('ada'));
    await user.click(ada.getByRole('button', { name: 'Promote' }));
    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(calls).toBe(0);
    expect(screen.queryByText('Promote ada to Admin?')).not.toBeInTheDocument();

    await user.click(ada.getByRole('button', { name: 'Promote' }));
    await user.click(screen.getByRole('button', { name: 'Confirm' }));

    await waitFor(() => {
      expect(calls).toBe(1);
    });
  });

  it('warns that demoting a configured account is undone by the next restart', async () => {
    const user = userEvent.setup();
    renderPage();

    const grace = within(await rowFor('grace'));
    await user.click(grace.getByRole('button', { name: 'Demote' }));

    // ADR 0022's trap, surfaced at the one moment somebody is about to walk into it rather than
    // after a restart has already undone their work.
    expect(await screen.findByRole('alert')).toHaveTextContent(/Admin__Usernames/);
  });

  it('offers no actions on the signed-in administrator\'s own row', async () => {
    server.use(
      http.get('/api/monitoring/users', () =>
        HttpResponse.json({
          items: [
            {
              id: anAdminSession.userId,
              username: anAdminSession.username,
              displayName: 'Me',
              role: 'Admin',
              roleIsConfigured: false,
              registeredAt: '2026-09-01T09:00:00+00:00',
              lastSeenAt: null,
            },
          ],
          totalCount: 1,
          page: 1,
        }),
      ),
    );
    renderPage();

    const me = within(await rowFor(anAdminSession.username));

    // Awaited, because the session query resolves independently of the user list: until it does,
    // the row does not yet know it is yours.
    expect(await me.findByText('You')).toBeInTheDocument();

    // The rail is the server's; this is the explanation. Both exist deliberately.
    expect(me.queryByRole('button', { name: 'Demote' })).not.toBeInTheDocument();
    expect(me.queryByRole('button', { name: 'Sign out' })).not.toBeInTheDocument();
  });

  it('renders the server refusal rather than an empty table', async () => {
    const user = userEvent.setup();
    server.use(
      http.post('/api/monitoring/users/:id/role', () =>
        HttpResponse.json(
          { title: 'Conflict', detail: 'That is the only administrator left.' },
          { status: 409 },
        ),
      ),
    );
    renderPage();

    const grace = within(await rowFor('grace'));
    await user.click(grace.getByRole('button', { name: 'Demote' }));
    await user.click(screen.getByRole('button', { name: 'Confirm' }));

    expect(await screen.findByText(/only administrator left/)).toBeInTheDocument();
  });

  it('shows what has been done to an account', async () => {
    const user = userEvent.setup();
    renderPage();

    const ada = within(await rowFor('ada'));
    await user.click(ada.getByRole('button', { name: 'History' }));

    // Scoped to the history table: the acting administrator here is also a listed account, so an
    // unscoped query for their username matches the row above as well.
    const history = within(await screen.findByRole('table', { name: /What has been done to ada/ }));
    expect(history.getByText('Promoted')).toBeInTheDocument();
    expect(history.getByText(anAdminSession.username)).toBeInTheDocument();
  });

  it('reports a failure to load the list', async () => {
    server.use(
      http.get('/api/monitoring/users', () =>
        HttpResponse.json({ title: 'Server error', detail: 'Nope.' }, { status: 500 }),
      ),
    );
    renderPage();

    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });
});
