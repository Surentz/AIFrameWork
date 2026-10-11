import { useQuery } from '@tanstack/react-query';
import type { UseQueryResult } from '@tanstack/react-query';
import { getAssistantStatus } from '../../api/assistant';
import type { ApiError } from '../../api/client';
import type { AssistantStatus } from './types';

export const assistantKeys = {
  all: ['assistant'] as const,
  status: () => [...assistantKeys.all, 'status'] as const,
};

/** Whether the assistant is switched on (Assistant__Enabled). Changes only with a deployment. */
export function useAssistantStatus(): UseQueryResult<AssistantStatus, ApiError> {
  return useQuery<AssistantStatus, ApiError>({
    queryKey: assistantKeys.status(),
    queryFn: getAssistantStatus,
    staleTime: Infinity,
  });
}
