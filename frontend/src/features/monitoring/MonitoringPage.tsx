import { useMonitoringAccess } from './queries';

/**
 * Phase 1 of docs/superpowers/plans/2026-09-20-monitoring-page.md: the gate, and nothing behind
 * it yet. Job runs, traffic and sign-in history arrive in phases 2 to 4, each as its own
 * drill-down route beneath this overview.
 */
export function MonitoringPage(): React.JSX.Element {
  const { data: access, isPending, error } = useMonitoringAccess();

  if (isPending) {
    return <p role="status">Loading monitoring…</p>;
  }

  if (error) {
    return (
      <p className="alert" role="alert">
        {error.message}
      </p>
    );
  }

  return (
    <section>
      <h1>Monitoring</h1>
      <p>
        Signed in as <strong>{access.username}</strong> ({access.role}).
      </p>
      <p>
        Operational data is not collected yet. Job runs and dead letters, application traffic, and
        sign-in history each arrive in a later phase, as their own pages beneath this one.
      </p>
    </section>
  );
}
