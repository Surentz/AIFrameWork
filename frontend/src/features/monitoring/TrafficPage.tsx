import { useState } from 'react';
import { Link } from 'react-router-dom';
import { TrafficChart } from './TrafficChart';
import type { ChartPoint, ChartSeries } from './TrafficChart';
import { useTrafficSeries, useTrafficSummary } from './queries';
import './monitoring.css';

const Windows = [15, 60, 240, 1440] as const;

const RateSeries: readonly ChartSeries[] = [
  { key: 'total', label: 'Requests', colorVar: '--viz-series-requests' },
  { key: 'errors', label: 'Errors', colorVar: '--viz-series-errors' },
];

const LatencySeries: readonly ChartSeries[] = [
  { key: 'p95', label: 'p95', colorVar: '--viz-series-requests' },
];

/**
 * Traffic: how much work the application is doing, how much of it fails, and how slow it is.
 *
 * Two charts rather than one with two scales. Requests and errors share a unit and belong on one
 * axis; latency is milliseconds and gets its own chart — a second y-axis would be the single most
 * common charting mistake, not a space saving.
 */
export function TrafficPage(): React.JSX.Element {
  const [windowMinutes, setWindowMinutes] = useState<number>(60);

  const summary = useTrafficSummary(windowMinutes);
  const series = useTrafficSeries(Math.min(windowMinutes, 1440));

  const points: ChartPoint[] =
    series.data?.points.map((point) => ({
      at: point.bucketStart,
      values: {
        total: Number(point.total),
        errors: Number(point.failed) + Number(point.faulted),
        p95:
          point.p95Ms === null || point.p95Ms === undefined
            ? null
            : Math.round(Number(point.p95Ms)),
      },
    })) ?? [];

  return (
    <section>
      <p>
        <Link to="/monitoring">← Monitoring</Link>
      </p>
      <h1>Traffic</h1>

      {/* Filters in one row above the charts, as ordinary form controls. */}
      <div className="filters">
        <label htmlFor="traffic-window">
          Window
          <select
            id="traffic-window"
            value={windowMinutes}
            onChange={(event) => {
              setWindowMinutes(Number(event.target.value));
            }}
          >
            {Windows.map((minutes) => (
              <option key={minutes} value={minutes}>
                {minutes < 60 ? `${String(minutes)} minutes` : `${String(minutes / 60)} hours`}
              </option>
            ))}
          </select>
        </label>
      </div>

      {summary.error && (
        <p className="alert" role="alert">
          {summary.error.message}
        </p>
      )}
      {series.error && (
        <p className="alert" role="alert">
          {series.error.message}
        </p>
      )}

      {summary.isSuccess && (
        <ul className="tiles">
          <li className="tile">
            <span className="tile__value">{Number(summary.data.requestsPerMinute).toFixed(1)}</span>
            <span className="tile__label">Per minute</span>
          </li>
          <li className={Number(summary.data.errorRate) > 0 ? 'tile tile--attention' : 'tile'}>
            <span className="tile__value">{(Number(summary.data.errorRate) * 100).toFixed(1)}%</span>
            <span className="tile__label">Error rate</span>
          </li>
          <li className="tile">
            <span className="tile__value">{formatMs(summary.data.overall.p95Ms)}</span>
            <span className="tile__label">p95</span>
          </li>
          <li className="tile">
            <span className="tile__value">{formatMs(summary.data.overall.p99Ms)}</span>
            <span className="tile__label">p99</span>
          </li>
        </ul>
      )}

      {series.isPending ? (
        <p role="status">Loading traffic…</p>
      ) : (
        <>
          <TrafficChart
            title="Requests and errors"
            unit="per minute"
            points={points}
            series={RateSeries}
          />
          <TrafficChart
            title="Latency"
            unit="p95, milliseconds"
            points={points}
            series={LatencySeries}
          />
        </>
      )}

      <h2>By endpoint and handler</h2>

      {!summary.isSuccess ? (
        summary.isPending && <p role="status">Loading the breakdown…</p>
      ) : summary.data.rows.length === 0 ? (
        <p>Nothing recorded in this window yet.</p>
      ) : (
        <table className="runs">
          <caption className="muted">Traffic by name</caption>
          <thead>
            <tr>
              <th scope="col">Kind</th>
              <th scope="col">Name</th>
              <th scope="col">Total</th>
              <th scope="col">Errors</th>
              <th scope="col">Mean</th>
              <th scope="col">p95</th>
              <th scope="col">p99</th>
            </tr>
          </thead>
          <tbody>
            {summary.data.rows.map((row) => (
              <tr key={`${row.kind}-${row.name}`}>
                <td>{row.kind}</td>
                <td>{row.name}</td>
                <td>{Number(row.total)}</td>
                <td>{Number(row.failed) + Number(row.faulted)}</td>
                <td>{formatMs(row.meanMs)}</td>
                <td>{formatMs(row.p95Ms)}</td>
                <td>{formatMs(row.p99Ms)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <p className="muted">
        Percentiles are interpolated from fixed latency buckets summed across every pod, so they
        describe the application rather than whichever replica answered. A mean cannot give a
        percentile, which is why both are shown.
      </p>
    </section>
  );
}

/**
 * `number | string` because that is genuinely what the contract says — ASP.NET Core's web JSON
 * defaults set AllowReadingFromString, so the document describes both and frontend/CLAUDE.md says
 * not to narrow it. Coerced here, at the boundary.
 */
function formatMs(value: number | string | null | undefined): string {
  return value === null || value === undefined
    ? '—'
    : `${Math.round(Number(value)).toLocaleString()} ms`;
}
