import { Link } from 'react-router-dom';
import { useJobHealth, useMonitoringAccess, useSignInHealth, useTrafficSummary } from './queries';
import { ErrorPanel } from '../../components/ErrorPanel';
import './monitoring.css';

/**
 * The overview: one screen answering "is anything wrong", with every tile group linking to the
 * sub-page that can answer "what, exactly".
 *
 * There is deliberately no tile for API/worker readiness or outbox depth. Both are already served
 * by the health endpoints the cluster itself probes, on their own schedule; a second, polled copy
 * here would eventually disagree with the one the platform acts on. See ADR 0021.
 */
export function MonitoringPage(): React.JSX.Element {
  const access = useMonitoringAccess();
  const health = useJobHealth();
  const signIns = useSignInHealth();
  const traffic = useTrafficSummary(60);

  if (access.isPending) {
    return <p role="status">Loading monitoring…</p>;
  }

  if (access.error) {
    return <ErrorPanel error={access.error} />;
  }

  return (
    <section>
      <h1>Monitoring</h1>
      <p>
        Signed in as <strong>{access.data.username}</strong> ({access.data.role}).
      </p>

      <h2>Jobs</h2>

      {health.error && <ErrorPanel error={health.error} />}

      {health.isSuccess ? (
        <ul className="tiles" aria-label="Job health">
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

      {signIns.error && <ErrorPanel error={signIns.error} />}

      {signIns.isSuccess && (
        <ul className="tiles" aria-label="Sign-in health">
          <Tile label="Succeeded" value={signIns.data.succeeded} />
          <Tile label="Bad credentials" value={signIns.data.badCredentials} />
          <Tile label="Unknown user" value={signIns.data.unknownUser} />
          <Tile label="Online now" value={signIns.data.activeUsers} />
        </ul>
      )}

      <p>
        <Link to="/monitoring/logins">Sign-in history, locked accounts and who is online</Link>
      </p>

      <h2>Traffic</h2>

      {traffic.error && <ErrorPanel error={traffic.error} />}

      {traffic.isSuccess && (
        <ul className="tiles" aria-label="Traffic">
          <li className="tile">
            <span className="tile__value">{Number(traffic.data.requestsPerMinute).toFixed(1)}</span>
            <span className="tile__label">Requests per minute</span>
          </li>
          <li className={Number(traffic.data.errorRate) > 0 ? 'tile tile--attention' : 'tile'}>
            <span className="tile__value">
              {(Number(traffic.data.errorRate) * 100).toFixed(1)}%
            </span>
            <span className="tile__label">Error rate</span>
          </li>
        </ul>
      )}

      <p>
        <Link to="/monitoring/traffic">Request rates, latency and the per-endpoint breakdown</Link>
      </p>

      <h2>Users</h2>

      <p>
        <Link to="/monitoring/users">Accounts, roles and sessions</Link>
      </p>

      <p className="muted">
        Job and sign-in counts cover the last 24 hours and traffic the last hour. Dead letters are
        the whole queue, because a message stuck for a week is exactly the one worth seeing.
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
  const attention =
    (label === 'Failed' ||
      label === 'Dead-lettered' ||
      label === 'Bad credentials' ||
      label === 'Unknown user') &&
    count > 0;

  return (
    <li className={attention ? 'tile tile--attention' : 'tile'}>
      <span className="tile__value">{count}</span>
      <span className="tile__label">{label}</span>
    </li>
  );
}
