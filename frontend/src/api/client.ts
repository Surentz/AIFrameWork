interface ProblemDetails {
  readonly title?: string;
  readonly detail?: string;
  readonly errors?: Record<string, string[]>;
  readonly traceId?: string;
}

export class ApiError extends Error {
  readonly status: number;
  readonly title: string;
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;

  /**
   * Every ProblemDetails from ResultExtensions.Problem and GlobalExceptionHandler carries one
   * (see AiFramework.Api), so an operator can find the matching backend log record — but a
   * non-JSON error body still falls back to `{}` below, so this stays optional rather than
   * defaulting to an empty string that would read as a real, searchable id.
   */
  readonly traceId: string | undefined;

  constructor(status: number, problem: ProblemDetails) {
    super(problem.detail ?? problem.title ?? `Request failed with status ${String(status)}.`);
    this.name = 'ApiError';
    this.status = status;
    this.title = problem.title ?? 'error';
    this.fieldErrors = problem.errors ?? {};
    this.traceId = problem.traceId;
  }
}

async function send(path: string, init?: RequestInit): Promise<Response> {
  // Seed the default, then let anything the caller passed overwrite it - Headers.set()
  // always wins over what's already there, so the default has to go in first.
  const headers = new Headers({ 'Content-Type': 'application/json' });
  new Headers(init?.headers).forEach((value, key) => {
    headers.set(key, value);
  });

  // No `credentials` option: fetch defaults to 'same-origin', and the session cookie is
  // same-origin because vite.config.ts proxies /api. Setting 'include' would be the change
  // needed if the API ever moved to its own origin.
  const response = await fetch(path, { ...init, headers });

  if (!response.ok) {
    // A non-JSON error body must not become a parse crash that hides the real status.
    const problem = (await response.json().catch(() => ({}))) as ProblemDetails;
    throw new ApiError(response.status, problem);
  }

  return response;
}

export async function request<T>(path: string, init?: RequestInit): Promise<T> {
  return (await (await send(path, init)).json()) as T;
}

/**
 * For the endpoints that answer 204. Same error handling as `request`, but no body to parse -
 * calling `request` for one of these throws on an empty body after a perfectly good response.
 */
export async function requestVoid(path: string, init?: RequestInit): Promise<void> {
  await send(path, init);
}

/** For the endpoints that answer a file. Same error handling as `request`. */
export async function requestBytes(path: string, init?: RequestInit): Promise<ArrayBuffer> {
  return (await send(path, init)).arrayBuffer();
}
