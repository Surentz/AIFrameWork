import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useSignInEvents, useSignInHealth } from './queries';
import type { SignInOutcome } from './types';
import './monitoring.css';

const Outcomes: readonly SignInOutcome[] = [
  'Succeeded',
  'BadCredentials',
  'LockedOut',
  'UnknownUser',
  'SignedOutEverywhere',
];

/** Mirrors the API's own default page size, so "Next" knows when there is no next. */
const PageSize = 50;

/**
 * The sign-in drill-down: the audit trail, who is locked out, and who is about.
 */
export function LoginsPage(): React.JSX.Element {
  const [outcome, setOutcome] = useState<SignInOutcome | undefined>(undefined);
  const [username, setUsername] = useState('');
  const [page, setPage] = useState(1);

  const events = useSignInEvents(outcome, username, page);

  return (
    <section>
      <p>
        <Link to="/monitoring">← Monitoring</Link>
      </p>
      <h1>Sign-ins</h1>

      <SignInSummary />

      <h2>Attempts</h2>

      <div className="filters">
        <label htmlFor="outcome">
          Outcome
          <select
            id="outcome"
            value={outcome ?? ''}
            onChange={(event) => {
              const next = event.target.value;
              setOutcome(next === '' ? undefined : (next as SignInOutcome));
              setPage(1);
            }}
          >
            <option value="">All</option>
            {Outcomes.map((value) => (
              <option key={value} value={value}>
                {value}
              </option>
            ))}
          </select>
        </label>

        <label htmlFor="username">
          Username
          <input
            id="username"
            value={username}
            placeholder="Any username"
            onChange={(event) => {
              setUsername(event.target.value);
              setPage(1);
            }}
          />
        </label>
      </div>

      {events.error && (
        <p className="alert" role="alert">
          {events.error.message}
        </p>
      )}

      {!events.isSuccess ? (
        events.isPending && <p role="status">Loading sign-ins…</p>
      ) : events.data.items.length === 0 ? (
        <p>No sign-in attempts match.</p>
      ) : (
        <>
          <table className="runs">
            <caption className="muted">
              Sign-in attempts: {Number(events.data.totalCount)} total
            </caption>
            <thead>
              <tr>
                <th scope="col">When</th>
                <th scope="col">Username</th>
                <th scope="col">Outcome</th>
                <th scope="col">Address</th>
                <th scope="col">Client</th>
              </tr>
            </thead>
            <tbody>
              {events.data.items.map((event) => (
                <tr key={event.id}>
                  <td>{new Date(event.at).toLocaleString()}</td>
                  <td>{event.usernameAttempted}</td>
                  <td>{event.outcome}</td>
                  <td>{event.ipAddress ?? '—'}</td>
                  <td className="runs__error">{event.userAgent ?? '—'}</td>
                </tr>
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
            Page {events.data.page}{' '}
            <button
              type="button"
              disabled={page * PageSize >= Number(events.data.totalCount)}
              onClick={() => {
                setPage(page + 1);
              }}
            >
              Next
            </button>
          </p>
        </>
      )}
    </section>
  );
}

function SignInSummary(): React.JSX.Element {
  const health = useSignInHealth();

  if (health.error) {
    return (
      <p className="alert" role="alert">
        {health.error.message}
      </p>
    );
  }

  if (!health.isSuccess) {
    return <p role="status">Loading sign-in health…</p>;
  }

  return (
    <>
      <ul className="tiles">
        <li className="tile">
          <span className="tile__value">{Number(health.data.succeeded)}</span>
          <span className="tile__label">Succeeded</span>
        </li>
        <li className={Number(health.data.badCredentials) > 0 ? 'tile tile--attention' : 'tile'}>
          <span className="tile__value">{Number(health.data.badCredentials)}</span>
          <span className="tile__label">Bad credentials</span>
        </li>
        <li className={Number(health.data.unknownUser) > 0 ? 'tile tile--attention' : 'tile'}>
          <span className="tile__value">{Number(health.data.unknownUser)}</span>
          <span className="tile__label">Unknown user</span>
        </li>
        <li className="tile">
          <span className="tile__value">{Number(health.data.activeUsers)}</span>
          <span className="tile__label">
            Online (last {Number(health.data.activeWindowMinutes)} min)
          </span>
        </li>
      </ul>

      <h2>Locked out</h2>
      {health.data.lockedOutUsers.length === 0 ? (
        <p>No accounts are locked.</p>
      ) : (
        <table className="runs">
          <caption className="muted">Locked accounts</caption>
          <thead>
            <tr>
              <th scope="col">Username</th>
              <th scope="col">Until</th>
            </tr>
          </thead>
          <tbody>
            {health.data.lockedOutUsers.map((user) => (
              <tr key={user.userId}>
                <td>{user.username}</td>
                <td>{new Date(user.lockedOutUntil).toLocaleString()}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <p className="muted">
        A lockout expires on its own; the window is fixed rather than sliding, so an attempt made
        while an account is locked does not extend it.
      </p>
    </>
  );
}
