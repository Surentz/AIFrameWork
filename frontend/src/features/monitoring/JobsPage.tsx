import { useState } from 'react';
import { Link } from 'react-router-dom';
import {
  useDeadLetters,
  useJobHealth,
  useJobRuns,
  useTraceLinkTemplate,
  useRetryDeadLetter,
  useTriggerJob,
} from './queries';
import type { JobRunStatus } from './types';
import { ErrorPanel } from '../../components/ErrorPanel';
import { TraceLink } from './TraceLink';
import './monitoring.css';

const Statuses: readonly JobRunStatus[] = ['Running', 'Succeeded', 'Failed'];

/** Mirrors the API's own default page size, so "Next" knows when there is no next. */
const PageSize = 50;

/**
 * The jobs drill-down: every attempt, the dead-letter queue, and the two operator actions.
 */
export function JobsPage(): React.JSX.Element {
  const [status, setStatus] = useState<JobRunStatus | undefined>(undefined);
  const [jobName, setJobName] = useState('');
  const [page, setPage] = useState(1);

  const runs = useJobRuns(status, jobName, page);
  const traceLinkTemplate = useTraceLinkTemplate();

  return (
    <section>
      <p>
        <Link to="/monitoring">← Monitoring</Link>
      </p>
      <h1>Job runs</h1>

      <div className="filters">
        <label htmlFor="run-status">
          Status
          <select
            id="run-status"
            value={status ?? ''}
            onChange={(event) => {
              const next = event.target.value;
              setStatus(next === '' ? undefined : (next as JobRunStatus));
              setPage(1);
            }}
          >
            <option value="">All</option>
            {Statuses.map((value) => (
              <option key={value} value={value}>
                {value}
              </option>
            ))}
          </select>
        </label>

        <label htmlFor="run-job-name">
          Job
          <input
            id="run-job-name"
            value={jobName}
            placeholder="Any job"
            onChange={(event) => {
              setJobName(event.target.value);
              setPage(1);
            }}
          />
        </label>
      </div>

      {runs.error && <ErrorPanel error={runs.error} />}

      {!runs.isSuccess ? (
        runs.isPending && <p role="status">Loading job runs…</p>
      ) : runs.data.items.length === 0 ? (
        <p>No job runs match.</p>
      ) : (
        <>
          <table className="runs">
            <caption className="muted">Job runs: {Number(runs.data.totalCount)} total</caption>
            <thead>
              <tr>
                <th scope="col">Job</th>
                <th scope="col">Lane</th>
                <th scope="col">Attempt</th>
                <th scope="col">Status</th>
                <th scope="col">Started</th>
                <th scope="col">Duration</th>
                <th scope="col">Error</th>
                <th scope="col">Trace</th>
              </tr>
            </thead>
            <tbody>
              {runs.data.items.map((run) => (
                <tr key={`${run.envelopeId}-${String(run.attempt)}`}>
                  <td>{run.jobName}</td>
                  <td>{run.lane ?? '—'}</td>
                  <td>{run.attempt}</td>
                  <td>{run.status}</td>
                  <td>{new Date(run.startedAt).toLocaleString()}</td>
                  <td>
                    {run.durationMs === null
                      ? '—'
                      : `${Number(run.durationMs).toLocaleString()} ms`}
                  </td>
                  <td className="runs__error">{run.error ?? ''}</td>
                  <td>
                    <TraceLink traceId={run.traceId} template={traceLinkTemplate} />
                  </td>
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
            Page {runs.data.page}{' '}
            <button
              type="button"
              disabled={page * PageSize >= Number(runs.data.totalCount)}
              onClick={() => {
                setPage(page + 1);
              }}
            >
              Next
            </button>
          </p>
        </>
      )}

      <DeadLetters />
      <TriggerJob />
    </section>
  );
}

function DeadLetters(): React.JSX.Element {
  const deadLetters = useDeadLetters();
  const retry = useRetryDeadLetter();

  return (
    <>
      <h2>Dead letters</h2>

      {deadLetters.error && <ErrorPanel error={deadLetters.error} />}
      {retry.error && <ErrorPanel error={retry.error} />}

      {!deadLetters.isSuccess ? (
        deadLetters.isPending && <p role="status">Loading dead letters…</p>
      ) : deadLetters.data.items.length === 0 ? (
        <p>Nothing has exhausted its retries.</p>
      ) : (
        <table className="runs">
          <caption className="muted">Dead-lettered messages</caption>
          <thead>
            <tr>
              <th scope="col">Message</th>
              <th scope="col">Exception</th>
              <th scope="col">Sent</th>
              <th scope="col">
                <span className="visually-hidden">Actions</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {deadLetters.data.items.map((letter) => (
              <tr key={letter.id}>
                <td>{letter.messageType}</td>
                <td className="runs__error">
                  {letter.exceptionType}: {letter.exceptionMessage}
                </td>
                <td>{new Date(letter.sentAt).toLocaleString()}</td>
                <td>
                  <button
                    type="button"
                    disabled={letter.replayable || retry.isPending}
                    onClick={() => {
                      retry.mutate(letter.id);
                    }}
                  >
                    {letter.replayable ? 'Queued to retry' : 'Retry'}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  );
}

function TriggerJob(): React.JSX.Element {
  const health = useJobHealth();
  const trigger = useTriggerJob();
  const [jobName, setJobName] = useState('');

  const options = health.data?.triggerableJobs ?? [];

  return (
    <>
      <h2>Run a scheduled job now</h2>

      {trigger.error && <ErrorPanel error={trigger.error} />}

      <div className="filters">
        <label htmlFor="trigger-job">
          Job
          <select
            id="trigger-job"
            value={jobName}
            onChange={(event) => {
              setJobName(event.target.value);
            }}
          >
            <option value="">Choose a job</option>
            {options.map((name) => (
              <option key={name} value={name}>
                {name}
              </option>
            ))}
          </select>
        </label>

        <button
          type="button"
          disabled={jobName === '' || trigger.isPending}
          onClick={() => {
            trigger.mutate(jobName);
          }}
        >
          Run now
        </button>
      </div>

      {trigger.isSuccess && (
        <p role="status">Queued. It runs on the worker; its outcome appears in the table above.</p>
      )}

      <p className="muted">
        Only scheduled jobs can be started this way — a job that takes arguments has none a trigger
        could supply.
      </p>
    </>
  );
}
