import { Link } from 'react-router-dom';
import { useJobHealth, useMonitoringAccess } from './queries';
import './monitoring.css';

/**
 * The overview. Phase 2 of docs/superpowers/plans/2026-09-20-monitoring-page.md fills the jobs
 * tiles; traffic and sign-in history arrive in phases 3 and 4 as their own tiles and sub-pages.
 */
export function MonitoringPage(): React.JSX.Element {
  const access = useMonitoringAccess();
  const health = useJobHealth();

  if (access.isPending) {
    return <p role="status">Loading monitoring…</p>;
  }

  if (access.error) {
    return (
      <p className="alert" role="alert">
        {access.error.message}
      </p>
    );
  }

  return (
    <section>
      <h1>Monitoring</h1>
      <p>
        Signed in as <strong>{access.data.username}</strong> ({access.data.role}).
      </p>

      <h2>Jobs</h2>

      {health.error && (
        <p className="alert" role="alert">
          {health.error.message}
        </p>
      )}

      {health.isSuccess ? (
        <ul className="tiles">
          <Tile label="Running" value={health.data.running} />
          <Tile label="Succeeded" value={health.data.succeeded} />
          <Tile label="Failed" value={health.data.failed} />
          <Tile label="Dead-lettered" value={health.data.deadLettered} />
        </ul>
      ) : (
        health.isPending && <p role="status">Loading job health…</p>
      )}

      <p>
        <Link to="/monitoring/jobs">Job runs and dead letters</Link>
      </p>

      <h2>Sign-ins</h2>
      <p>
        <Link to="/monitoring/logins">Sign-in history, locked accounts and who is online</Link>
      </p>

      <p className="muted">
        Counts cover the last 24 hours, except dead letters — those are the whole queue, because a
        message stuck for a week is exactly the one worth seeing.
      </p>
    </section>
  );
}

interface TileProps {
  readonly label: string;
  /**
   * `number | string` because that is genuinely what the contract says: ASP.NET Core's web JSON
   * defaults set AllowReadingFromString, so the API accepts `"5"` as well as `5` and the
   * generated document describes both. frontend/CLAUDE.md says not to narrow it to look tidier —
   * so it is coerced here, at the boundary, rather than edited out of the schema.
   */
  readonly value: number | string;
}

function Tile({ label, value }: TileProps): React.JSX.Element {
  const count = Number(value);

  // A count that is not zero when zero is the healthy value. Colour is not the only signal: the
  // label beside it says what it counts, so this reads the same without relying on hue.
  const attention = (label === 'Failed' || label === 'Dead-lettered') && count > 0;

  return (
    <li className={attention ? 'tile tile--attention' : 'tile'}>
      <span className="tile__value">{count}</span>
      <span className="tile__label">{label}</span>
    </li>
  );
}
