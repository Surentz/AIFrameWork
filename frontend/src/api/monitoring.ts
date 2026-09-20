import type {
  DeadLetterPage,
  JobHealth,
  JobRunPage,
  JobRunStatus,
  MonitoringAccess,
  SignInEventPage,
  SignInHealth,
  SignInOutcome,
} from '../features/monitoring/types';
import { request, requestVoid } from './client';

export function getMonitoringAccess(): Promise<MonitoringAccess> {
  return request<MonitoringAccess>('/api/monitoring/access');
}

export function getJobHealth(): Promise<JobHealth> {
  return request<JobHealth>('/api/monitoring/jobs/health');
}

// The `| undefined` on each member is required, not noise: exactOptionalPropertyTypes is on, so
// `{ status }` where status is `JobRunStatus | undefined` does not satisfy a bare `status?: …`.
export function listJobRuns(params: {
  status?: JobRunStatus | undefined;
  jobName?: string | undefined;
  page?: number | undefined;
}): Promise<JobRunPage> {
  const search = new URLSearchParams();
  search.set('page', String(params.page ?? 1));
  if (params.status !== undefined) {
    search.set('status', params.status);
  }
  if (params.jobName !== undefined && params.jobName !== '') {
    search.set('jobName', params.jobName);
  }

  return request<JobRunPage>(`/api/monitoring/jobs/runs?${search.toString()}`);
}

export function listDeadLetters(): Promise<DeadLetterPage> {
  return request<DeadLetterPage>('/api/monitoring/jobs/dead-letters');
}

export function retryDeadLetter(messageId: string): Promise<void> {
  return requestVoid(`/api/monitoring/jobs/dead-letters/${messageId}/retry`, { method: 'POST' });
}

export function triggerJob(jobName: string): Promise<void> {
  return requestVoid('/api/monitoring/jobs/trigger', {
    method: 'POST',
    body: JSON.stringify({ jobName }),
  });
}

export function getSignInHealth(): Promise<SignInHealth> {
  return request<SignInHealth>('/api/monitoring/sign-ins/health');
}

export function listSignInEvents(params: {
  outcome?: SignInOutcome | undefined;
  username?: string | undefined;
  page?: number | undefined;
}): Promise<SignInEventPage> {
  const search = new URLSearchParams();
  search.set('page', String(params.page ?? 1));
  if (params.outcome !== undefined) {
    search.set('outcome', params.outcome);
  }
  if (params.username !== undefined && params.username !== '') {
    search.set('username', params.username);
  }

  return request<SignInEventPage>(`/api/monitoring/sign-ins?${search.toString()}`);
}
