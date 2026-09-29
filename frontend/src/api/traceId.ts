/**
 * The backend hands out trace ids in two shapes, and both are right for where they come from:
 * a ProblemDetails carries `Activity.Id`, the full W3C traceparent
 * (`00-<trace id>-<span id>-<flags>`, ASP.NET Core's own convention), while the monitoring tables
 * store the bare 32-hex trace id. A log store is searched by the bare id, so everything shown or
 * linked goes through here first.
 */
const Bare = /^[0-9a-f]{32}$/;
const TraceParent = /^[0-9a-f]{2}-([0-9a-f]{32})-[0-9a-f]{16}-[0-9a-f]{2}$/;
const Invalid = '0'.repeat(32);

/** The placeholder a configured template marks the id's position with (MonitoringPageOptions). */
const Placeholder = '{traceId}';

/**
 * The bare, lowercase trace id from either shape, or undefined for anything else — including
 * `HttpContext.TraceIdentifier`, the fallback a ProblemDetails carries when no trace was running,
 * which appears in no log record and so is not worth handing anyone to search for.
 */
export function normaliseTraceId(value: string | null | undefined): string | undefined {
  if (!value) {
    return undefined;
  }

  const lower = value.trim().toLowerCase();
  const id = Bare.test(lower) ? lower : TraceParent.exec(lower)?.[1];

  return id === undefined || id === Invalid ? undefined : id;
}

/**
 * The template with the id filled in, or undefined when there is no template, no usable id, or
 * the result is not an http(s) URL. The API already refuses an unsafe template at startup; this
 * is the second check, at the point the value becomes an href.
 */
export function buildTraceUrl(
  template: string | null | undefined,
  traceId: string | null | undefined,
): string | undefined {
  const id = normaliseTraceId(traceId);

  if (!template || id === undefined) {
    return undefined;
  }

  const candidate = template.replaceAll(Placeholder, encodeURIComponent(id));

  return URL.canParse(candidate) && /^https?:$/.test(new URL(candidate).protocol)
    ? candidate
    : undefined;
}
