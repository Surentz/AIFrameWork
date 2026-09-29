import { buildTraceUrl, normaliseTraceId } from '../../api/traceId';

interface TraceLinkProps {
  readonly traceId: string | null | undefined;
  /** Monitoring__TraceLinkTemplate, from the access response; undefined when none is set. */
  readonly template: string | undefined;
}

/**
 * A row's way into the log store (ADR 0021's deep link). A link when a template is configured,
 * the id as selectable text when not — the operator can still paste it into a search — and a dash
 * for a row recorded with no trace running.
 */
export function TraceLink({ traceId, template }: TraceLinkProps): React.JSX.Element {
  const id = normaliseTraceId(traceId);

  if (id === undefined) {
    return <span>—</span>;
  }

  const href = buildTraceUrl(template, id);
  const code = <code className="trace-id">{id}</code>;

  return href === undefined ? (
    code
  ) : (
    <a
      href={href}
      target="_blank"
      rel="noreferrer"
      aria-label={`Open trace ${id} in the log store (opens in a new tab)`}
    >
      {code}
    </a>
  );
}
