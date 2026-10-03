import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useSession } from '../auth/queries';
import {
  useChangeUserRole,
  useSignOutUser,
  useTraceLinkTemplate,
  useUserActions,
  useUsers,
} from './queries';
import type { AdministeredUser } from './types';
import { ErrorPanel } from '../../components/ErrorPanel';
import { TraceLink } from './TraceLink';
import './monitoring.css';

/** Mirrors the API's own default page size, so "Next" knows when there is no next. */
const PageSize = 50;

/** What a row is currently asking the operator to confirm, if anything. */
type Pending = { readonly kind: 'promote' | 'demote' | 'sign-out' } | undefined;

/**
 * User administration: who holds the administrator role, and revoking someone's sessions.
 *
 * The rails are enforced by the server (ADR 0022) — this hides the actions it knows will be
 * refused, which is an explanation rather than the mechanism. Where the two could disagree, the
 * server is right and its error is rendered.
 */
export function UsersPage(): React.JSX.Element {
  const [draft, setDraft] = useState('');
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);

  const users = useUsers(search, page);

  return (
    <section>
      <p>
        <Link to="/monitoring">← Monitoring</Link>
      </p>
      <h1>Users</h1>

      <form
        className="filters"
        onSubmit={(event) => {
          // Submitted rather than searched per keystroke: every one of these is an uncached read
          // (ADR 0021), and the list is not something an operator scrubs through.
          event.preventDefault();
          setSearch(draft);
          setPage(1);
        }}
      >
        <label htmlFor="user-search">
          Search
          <input
            id="user-search"
            type="search"
            value={draft}
            placeholder="Username or display name"
            onChange={(event) => {
              setDraft(event.target.value);
            }}
          />
        </label>
        <button type="submit">Search</button>
      </form>

      {users.error && <ErrorPanel error={users.error} />}

      {users.isPending && <p role="status">Loading users…</p>}

      {users.isSuccess && users.data.items.length === 0 && <p>No accounts match that search.</p>}

      {users.isSuccess && users.data.items.length > 0 && (
        <>
          <table className="runs">
            <caption className="muted">Accounts, most recently seen first</caption>
            <thead>
              <tr>
                <th scope="col">Username</th>
                <th scope="col">Name</th>
                <th scope="col">Role</th>
                <th scope="col">Last seen</th>
                <th scope="col">Actions</th>
              </tr>
            </thead>
            <tbody>
              {users.data.items.map((user) => (
                <UserRow key={user.id} user={user} />
              ))}
            </tbody>
          </table>

          <p>
            <button
              type="button"
              disabled={page === 1}
              onClick={() => {
                setPage(page - 1);
              }}
            >
              Previous
            </button>{' '}
            Page {users.data.page}{' '}
            <button
              type="button"
              disabled={page * PageSize >= Number(users.data.totalCount)}
              onClick={() => {
                setPage(page + 1);
              }}
            >
              Next
            </button>
          </p>
        </>
      )}

      <p className="muted">
        Demoting an account does not sign it out: the role is read from the database on every
        request, so the change takes effect on that user&apos;s next one. Use{' '}
        <strong>Sign out</strong> to end their sessions.
      </p>
    </section>
  );
}

interface UserRowProps {
  readonly user: AdministeredUser;
}

function UserRow({ user }: UserRowProps): React.JSX.Element {
  const [pending, setPending] = useState<Pending>(undefined);
  const [showHistory, setShowHistory] = useState(false);

  const session = useSession();
  const changeRole = useChangeUserRole();
  const signOut = useSignOutUser();

  // The two rails, mirrored from the server so the buttons are not offering what it will refuse.
  // The server still enforces both — see ChangeUserRoleHandler.
  const isSelf = session.data?.userId === user.id;
  const isAdmin = user.role === 'Admin';

  const error = changeRole.error ?? signOut.error;

  return (
    <>
      <tr>
        <td>{user.username}</td>
        <td>{user.displayName}</td>
        <td>
          {user.role}
          {user.roleIsConfigured && (
            <>
              {' '}
              <span
                className="badge"
                title="Named in Admin__Usernames. Demoting this account is undone at the next API restart unless the name is removed there too."
              >
                configured
              </span>
            </>
          )}
        </td>
        <td>
          {user.lastSeenAt === null || user.lastSeenAt === undefined
            ? 'Never'
            : new Date(user.lastSeenAt).toLocaleString()}
        </td>
        <td>
          {isSelf ? (
            <span className="muted">You</span>
          ) : pending === undefined ? (
            <>
              <button
                type="button"
                onClick={() => {
                  setPending({ kind: isAdmin ? 'demote' : 'promote' });
                }}
              >
                {isAdmin ? 'Demote' : 'Promote'}
              </button>{' '}
              <button
                type="button"
                onClick={() => {
                  setPending({ kind: 'sign-out' });
                }}
              >
                Sign out
              </button>{' '}
            </>
          ) : (
            <Confirm
              user={user}
              pending={pending}
              busy={changeRole.isPending || signOut.isPending}
              onCancel={() => {
                setPending(undefined);
              }}
              onConfirm={() => {
                const done = (): void => {
                  setPending(undefined);
                };

                if (pending.kind === 'sign-out') {
                  signOut.mutate(user.id, { onSuccess: done });
                } else {
                  changeRole.mutate(
                    { userId: user.id, role: pending.kind === 'promote' ? 'Admin' : 'Member' },
                    { onSuccess: done },
                  );
                }
              }}
            />
          )}
          <button
            type="button"
            aria-expanded={showHistory}
            onClick={() => {
              setShowHistory(!showHistory);
            }}
          >
            History
          </button>
        </td>
      </tr>

      {error && (
        <tr>
          {/* role="alert" on a <p> inside the cell, not on the <td> itself: a table cell already
              has an implicit role, and overriding it would take the row out of the table's own
              semantics for a screen reader. */}
          <td colSpan={5}>
            <ErrorPanel error={error} />
          </td>
        </tr>
      )}

      {showHistory && (
        <tr>
          <td colSpan={5}>
            <History userId={user.id} username={user.username} />
          </td>
        </tr>
      )}
    </>
  );
}

interface ConfirmProps {
  readonly user: AdministeredUser;
  readonly pending: NonNullable<Pending>;
  readonly busy: boolean;
  readonly onConfirm: () => void;
  readonly onCancel: () => void;
}

/**
 * Names the account and the change in words, so a misclick in a table row cannot act on the
 * wrong person.
 *
 * Inline and non-modal rather than a `<dialog>`: the question belongs beside the row it concerns,
 * there is no focus to trap, and it needs no polyfill to be readable by assistive technology.
 */
function Confirm({ user, pending, busy, onConfirm, onCancel }: ConfirmProps): React.JSX.Element {
  const question =
    pending.kind === 'promote'
      ? `Promote ${user.username} to Admin?`
      : pending.kind === 'demote'
        ? `Demote ${user.username} to Member?`
        : `Sign ${user.username} out of every session?`;

  return (
    <span role="group" aria-label={question}>
      <strong>{question}</strong>{' '}
      {pending.kind === 'demote' && user.roleIsConfigured && (
        <>
          <span className="alert" role="alert">
            {user.username} is named in Admin__Usernames, so the next API restart promotes them
            again. Remove the name there as well.
          </span>{' '}
        </>
      )}
      <button type="button" disabled={busy} onClick={onConfirm}>
        Confirm
      </button>{' '}
      <button type="button" disabled={busy} onClick={onCancel}>
        Cancel
      </button>{' '}
    </span>
  );
}

interface HistoryProps {
  readonly userId: string;
  readonly username: string;
}

function History({ userId, username }: HistoryProps): React.JSX.Element {
  const actions = useUserActions(userId);
  const traceLinkTemplate = useTraceLinkTemplate();

  if (actions.error) {
    return <ErrorPanel error={actions.error} />;
  }

  if (!actions.isSuccess) {
    return <p role="status">Loading history…</p>;
  }

  if (actions.data.items.length === 0) {
    return <p className="muted">Nothing has been done to {username}.</p>;
  }

  return (
    <table className="runs">
      <caption className="muted">What has been done to {username}</caption>
      <thead>
        <tr>
          <th scope="col">When</th>
          <th scope="col">Action</th>
          <th scope="col">By</th>
          <th scope="col">From</th>
          <th scope="col">Trace</th>
        </tr>
      </thead>
      <tbody>
        {actions.data.items.map((action) => (
          <tr key={action.id}>
            <td>{new Date(action.at).toLocaleString()}</td>
            <td>{action.kind}</td>
            <td>{action.actorUsername}</td>
            <td>{action.ipAddress ?? '—'}</td>
            <td>
              <TraceLink traceId={action.traceId} template={traceLinkTemplate} />
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
