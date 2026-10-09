import { Link } from 'react-router-dom';
import { useExternalSystems } from './queries';
import type { ExternalSystemRow } from './types';
import { ErrorPanel } from '../../components/ErrorPanel';
import './monitoring.css';

const DayMs = 24 * 60 * 60 * 1000;
const ExpiryWarningDays = 30;

/**
 * External systems: what the worker last saw of each partner (ADR 0032), and the last hour of
 * calls to it. A stale row means the worker stopped checking — its last status is history.
 */
export function IntegrationsPage(): React.JSX.Element {
  const systems = useExternalSystems();

  return (
    <section>
      <h1>External systems</h1>
      <p>
        <Link to="/monitoring">Back to monitoring</Link>
      </p>

      {systems.isPending && <p role="status">Loading external systems…</p>}
      {systems.error && <ErrorPanel error={systems.error} />}

      {systems.isSuccess && systems.data.systems.length === 0 && (
        <p>No external system is configured.</p>
      )}

      {systems.isSuccess && systems.data.systems.length > 0 && (
        <table className="runs">
          <caption className="muted">Status as of the last check; calls over the last hour</caption>
          <thead>
            <tr>
              <th scope="col">System</th>
              <th scope="col">Status</th>
              <th scope="col">Checked</th>
              <th scope="col">Certificate</th>
              <th scope="col">Token</th>
              <th scope="col">Calls</th>
              <th scope="col">Attempts</th>
              <th scope="col">Errors</th>
              <th scope="col">p95</th>
            </tr>
          </thead>
          <tbody>
            {systems.data.systems.map((row) => (
              <tr key={row.name}>
                <th scope="row">{row.name}</th>
                <td className={statusClass(row)} title={row.description ?? undefined}>
                  {statusText(row)}
                </td>
                <td>{row.checkedAt ? new Date(row.checkedAt).toLocaleTimeString() : '—'}</td>
                <CertificateCell notAfter={row.certificateNotAfter} now={systems.dataUpdatedAt} />
                <td>{row.tokenOk === null || row.tokenOk === undefined ? '—' : row.tokenOk ? 'OK' : 'Failing'}</td>
                <td>{Number(row.calls)}</td>
                <td>{Number(row.attempts)}</td>
                <td>{Number(row.failed) + Number(row.faulted)}</td>
                <td>{row.p95Ms === null || row.p95Ms === undefined ? '—' : `${String(Math.round(Number(row.p95Ms)))} ms`}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}

function statusText(row: ExternalSystemRow): string {
  if (row.stale) {
    return 'Stale — the worker has stopped checking';
  }
  return row.state ?? 'Not checked yet';
}

function statusClass(row: ExternalSystemRow): string {
  return row.stale || row.state === 'Unhealthy' || row.state === 'Degraded' ? 'attention' : '';
}

interface CertificateCellProps {
  readonly notAfter: string | null | undefined;
  /** The time of the last fetch, passed in so render stays pure. */
  readonly now: number;
}

function CertificateCell({ notAfter, now }: CertificateCellProps): React.JSX.Element {
  if (!notAfter) {
    return <td>—</td>;
  }
  const days = Math.floor((new Date(notAfter).getTime() - now) / DayMs);
  if (days < 0) {
    return <td className="attention">Expired</td>;
  }
  return <td className={days < ExpiryWarningDays ? 'attention' : ''}>{`in ${String(days)} days`}</td>;
}
