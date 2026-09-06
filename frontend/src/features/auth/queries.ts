import { useMutation, useQueryClient } from '@tanstack/react-query';
import type { UseMutationResult } from '@tanstack/react-query';
import { login } from '../../api/auth';
import type { ApiError } from '../../api/client';
import type { Credentials, Session } from './types';

export const authKeys = {
  all: ['auth'] as const,
  session: () => [...authKeys.all, 'session'] as const,
};

export function useLogin(): UseMutationResult<Session, ApiError, Credentials> {
  const client = useQueryClient();

  return useMutation({
    mutationFn: login,
    onSuccess: (session) => {
      // Seeded rather than invalidated: the response *is* the session, so refetching it would
      // be a second round trip for a value already in hand.
      client.setQueryData(authKeys.session(), session);
    },
  });
}
