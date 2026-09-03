interface ProblemDetails {
  readonly title?: string;
  readonly detail?: string;
  readonly errors?: Record<string, string[]>;
}

export class ApiError extends Error {
  readonly status: number;
  readonly title: string;
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;

  constructor(status: number, problem: ProblemDetails) {
    super(problem.detail ?? problem.title ?? `Request failed with status ${String(status)}.`);
    this.name = 'ApiError';
    this.status = status;
    this.title = problem.title ?? 'error';
    this.fieldErrors = problem.errors ?? {};
  }
}

export async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const headers = new Headers(init?.headers);
  headers.set('Content-Type', 'application/json');

  const response = await fetch(path, { ...init, headers });

  if (!response.ok) {
    // A non-JSON error body must not become a parse crash that hides the real status.
    const problem = (await response.json().catch(() => ({}))) as ProblemDetails;
    throw new ApiError(response.status, problem);
  }

  return (await response.json()) as T;
}
