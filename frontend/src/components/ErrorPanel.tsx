import { ApiError } from '../api/client';
import { normaliseTraceId } from '../api/traceId';

interface ErrorPanelProps {
  readonly error: Error;
  /** Words that say what failed, shown before the message ("The products could not be loaded."). */
  readonly lead?: string;
  /** A layout modifier added beside `alert` (`orders__error`, `shell__error`, …). */
  readonly className?: string;
}

/**
 * How every failed query and mutation is shown. The message, and — for a server failure only —
 * the trace id as a reference, so the person who hit it can quote something an operator can
 * search the log store for.
 *
 * Only for 5xx: a 4xx is the caller's to fix (a wrong password, an invalid quantity), and a
 * reference there reads as "contact support" for something support cannot help with.
 */
export function ErrorPanel({ error, lead, className }: ErrorPanelProps): React.JSX.Element {
  const reference =
    error instanceof ApiError && error.status >= 500 ? normaliseTraceId(error.traceId) : undefined;

  return (
    <p className={className === undefined ? 'alert' : `alert ${className}`} role="alert">
      {lead !== undefined && `${lead} `}
      {error.message}
      {reference !== undefined && (
        <span className="alert__reference">
          {' '}
          Reference: <code>{reference}</code>
        </span>
      )}
    </p>
  );
}
