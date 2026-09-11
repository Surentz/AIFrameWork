import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { UseMutationResult, UseQueryResult } from '@tanstack/react-query';
import {
  changePassword,
  getSession,
  login,
  logout,
  register,
  signOutEverywhere,
} from '../../api/auth';
import { ApiError } from '../../api/client';
import type { Credentials, PasswordChange, Registration, Session } from './types';

export const authKeys = {
  all: ['auth'] as const,
  session: () => [...authKeys.all, 'session'] as const,
};

/**
 * The signed-in user, or null. A 401 is the ordinary answer for a signed-out visitor rather than
 * a failure, so it resolves to null instead of rejecting — otherwise every guarded route would
 * have to tell "signed out" apart from "the request broke" by reading a status code.
 */
export function useSession(): UseQueryResult<Session | null, ApiError> {
  return useQuery({
    queryKey: authKeys.session(),
    queryFn: async () => {
      try {
        return await getSession();
      } catch (error) {
        if (error instanceof ApiError && error.status === 401) {
          return null;
        }
        throw error;
      }
    },
    // Retrying a 401 just delays the redirect to the login page.
    retry: false,
  });
}

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

export function useRegister(): UseMutationResult<Session, ApiError, Registration> {
  const client = useQueryClient();

  return useMutation({
    mutationFn: register,
    onSuccess: (session) => {
      client.setQueryData(authKeys.session(), session);
    },
  });
}

export function useLogout(): UseMutationResult<void, ApiError, void> {
  const client = useQueryClient();

  return useMutation({
    mutationFn: logout,
    onSuccess: async () => {
      // Everything cached was fetched as the signed-out-from user; orders included.
      client.setQueryData(authKeys.session(), null);
      await client.invalidateQueries();
    },
  });
}

export function useChangePassword(): UseMutationResult<void, ApiError, PasswordChange> {
  return useMutation({ mutationFn: changePassword });
}

/**
 * Ends every session for the current user, this browser included. The cache teardown matches
 * useLogout exactly: the server has already cleared this browser's cookie, so anything still
 * cached was fetched as a user who is no longer signed in here.
 */
export function useSignOutEverywhere(): UseMutationResult<void, ApiError, void> {
  const client = useQueryClient();

  return useMutation({
    mutationFn: signOutEverywhere,
    onSuccess: async () => {
      client.setQueryData(authKeys.session(), null);
      await client.invalidateQueries();
    },
  });
}
