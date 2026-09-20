import { useQuery } from '@tanstack/react-query';
import type { UseQueryResult } from '@tanstack/react-query';
import type { ApiError } from '../../api/client';
import { getMonitoringAccess } from '../../api/monitoring';
import type { MonitoringAccess } from './types';

export const monitoringKeys = {
  all: ['monitoring'] as const,
  access: () => [...monitoringKeys.all, 'access'] as const,
};

/**
 * Confirms access against the server rather than trusting the role the session carries. The two
 * agree in every ordinary case; where they do not — a demotion landing between one request and
 * the next — the server is right, and this is what surfaces that rather than rendering a page of
 * failed panels.
 */
export function useMonitoringAccess(): UseQueryResult<MonitoringAccess, ApiError> {
  return useQuery({
    queryKey: monitoringKeys.access(),
    queryFn: getMonitoringAccess,
    retry: false,
  });
}
