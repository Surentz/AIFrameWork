import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { UseMutationResult, UseQueryResult } from '@tanstack/react-query';
import type { ApiError } from '../../api/client';
import {
  getJobHealth,
  getMonitoringAccess,
  getSignInHealth,
  getTrafficSeries,
  getTrafficSummary,
  listDeadLetters,
  listJobRuns,
  listSignInEvents,
  retryDeadLetter,
  triggerJob,
} from '../../api/monitoring';
import type {
  DeadLetterPage,
  JobHealth,
  JobRunPage,
  JobRunStatus,
  MonitoringAccess,
  SignInEventPage,
  SignInHealth,
  SignInOutcome,
  TrafficSeries,
  TrafficSummary,
} from './types';

export const monitoringKeys = {
  all: ['monitoring'] as const,
  access: () => [...monitoringKeys.all, 'access'] as const,
  jobs: () => [...monitoringKeys.all, 'jobs'] as const,
  jobHealth: () => [...monitoringKeys.jobs(), 'health'] as const,
  jobRuns: (status: JobRunStatus | undefined, jobName: string, page: number) =>
    [...monitoringKeys.jobs(), 'runs', status ?? 'all', jobName, page] as const,
  deadLetters: () => [...monitoringKeys.jobs(), 'dead-letters'] as const,
  signIns: () => [...monitoringKeys.all, 'sign-ins'] as const,
  signInHealth: () => [...monitoringKeys.signIns(), 'health'] as const,
  signInEvents: (outcome: SignInOutcome | undefined, username: string, page: number) =>
    [...monitoringKeys.signIns(), 'events', outcome ?? 'all', username, page] as const,
  traffic: () => [...monitoringKeys.all, 'traffic'] as const,
  trafficSummary: (windowMinutes: number) =>
    [...monitoringKeys.traffic(), 'summary', windowMinutes] as const,
  trafficSeries: (windowMinutes: number) =>
    [...monitoringKeys.traffic(), 'series', windowMinutes] as const,
};

/**
 * How often the operator-facing panels re-ask. Ten seconds is the plan's figure: fast enough that
 * a dashboard left open is worth looking at, slow enough that it is not a load generator of its
 * own. None of these queries is cached server-side (ADR 0021), so every poll is a real read.
 */
const RefreshMs = 10_000;

/**
 * Confirms access against the server rather than trusting the role the session carries. The two
 * agree in every ordinary case; where they do not — a demotion landing between one request and
 * the next — the server is right.
 */
export function useMonitoringAccess(): UseQueryResult<MonitoringAccess, ApiError> {
  return useQuery({
    queryKey: monitoringKeys.access(),
    queryFn: getMonitoringAccess,
    retry: false,
  });
}

export function useJobHealth(): UseQueryResult<JobHealth, ApiError> {
  return useQuery({
    queryKey: monitoringKeys.jobHealth(),
    queryFn: getJobHealth,
    refetchInterval: RefreshMs,
    retry: false,
  });
}

export function useJobRuns(
  status: JobRunStatus | undefined,
  jobName: string,
  page: number,
): UseQueryResult<JobRunPage, ApiError> {
  return useQuery({
    queryKey: monitoringKeys.jobRuns(status, jobName, page),
    queryFn: () => listJobRuns({ status, jobName, page }),
    refetchInterval: RefreshMs,
    retry: false,
  });
}

export function useDeadLetters(): UseQueryResult<DeadLetterPage, ApiError> {
  return useQuery({
    queryKey: monitoringKeys.deadLetters(),
    queryFn: listDeadLetters,
    refetchInterval: RefreshMs,
    retry: false,
  });
}

/**
 * Both mutations invalidate the whole jobs subtree rather than one key: retrying a dead letter
 * removes it from that list AND produces a new run, and triggering a job produces a run and moves
 * the health counts. Invalidating precisely would mean naming every consequence.
 */
export function useRetryDeadLetter(): UseMutationResult<void, ApiError, string> {
  const client = useQueryClient();

  return useMutation({
    mutationFn: retryDeadLetter,
    onSuccess: () => client.invalidateQueries({ queryKey: monitoringKeys.jobs() }),
  });
}

export function useTriggerJob(): UseMutationResult<void, ApiError, string> {
  const client = useQueryClient();

  return useMutation({
    mutationFn: triggerJob,
    onSuccess: () => client.invalidateQueries({ queryKey: monitoringKeys.jobs() }),
  });
}

/**
 * The logins panels refresh more slowly than the jobs ones. A sign-in is a human-paced event and
 * the table is personal data, so re-reading it every ten seconds would be cost without insight.
 */
const SignInRefreshMs = 30_000;

export function useSignInHealth(): UseQueryResult<SignInHealth, ApiError> {
  return useQuery({
    queryKey: monitoringKeys.signInHealth(),
    queryFn: getSignInHealth,
    refetchInterval: SignInRefreshMs,
    retry: false,
  });
}

export function useSignInEvents(
  outcome: SignInOutcome | undefined,
  username: string,
  page: number,
): UseQueryResult<SignInEventPage, ApiError> {
  return useQuery({
    queryKey: monitoringKeys.signInEvents(outcome, username, page),
    queryFn: () => listSignInEvents({ outcome, username, page }),
    refetchInterval: SignInRefreshMs,
    retry: false,
  });
}

/**
 * Traffic buckets are written once a minute, so asking more often than that returns the same
 * numbers. Thirty seconds keeps a dashboard left open honest without polling for nothing.
 */
const TrafficRefreshMs = 30_000;

export function useTrafficSummary(
  windowMinutes: number,
): UseQueryResult<TrafficSummary, ApiError> {
  return useQuery({
    queryKey: monitoringKeys.trafficSummary(windowMinutes),
    queryFn: () => getTrafficSummary(windowMinutes),
    refetchInterval: TrafficRefreshMs,
    retry: false,
  });
}

export function useTrafficSeries(windowMinutes: number): UseQueryResult<TrafficSeries, ApiError> {
  return useQuery({
    queryKey: monitoringKeys.trafficSeries(windowMinutes),
    queryFn: () => getTrafficSeries(windowMinutes),
    refetchInterval: TrafficRefreshMs,
    retry: false,
  });
}
