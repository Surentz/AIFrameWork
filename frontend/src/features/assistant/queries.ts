import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { UseMutationResult, UseQueryResult } from '@tanstack/react-query';
import {
  deleteConversation,
  getAssistantStatus,
  getConversation,
  listConversations,
  renameConversation,
} from '../../api/assistant';
import type { ApiError } from '../../api/client';
import type { AssistantStatus, ConversationSummary, SavedConversation } from './types';

export const assistantKeys = {
  all: ['assistant'] as const,
  status: () => [...assistantKeys.all, 'status'] as const,
  conversations: () => [...assistantKeys.all, 'conversations'] as const,
  conversation: (id: string) => [...assistantKeys.conversations(), id] as const,
};

/** Whether the assistant is switched on (Assistant__Enabled). Changes only with a deployment. */
export function useAssistantStatus(): UseQueryResult<AssistantStatus, ApiError> {
  return useQuery<AssistantStatus, ApiError>({
    queryKey: assistantKeys.status(),
    queryFn: getAssistantStatus,
    staleTime: Infinity,
  });
}

/** The sidebar. Every saved answer, rename and delete invalidates it. */
export function useConversations(): UseQueryResult<readonly ConversationSummary[], ApiError> {
  return useQuery<readonly ConversationSummary[], ApiError>({
    queryKey: assistantKeys.conversations(),
    queryFn: listConversations,
  });
}

/**
 * One saved conversation, read once when it is opened: the chat it seeds owns the messages from
 * then on. No cache across visits (gcTime 0) and no background refetch, so reopening always shows
 * what was saved and a refetch can never replace a chat that is streaming.
 */
export function useSavedConversation(id: string): UseQueryResult<SavedConversation, ApiError> {
  return useQuery<SavedConversation, ApiError>({
    queryKey: assistantKeys.conversation(id),
    queryFn: () => getConversation(id),
    gcTime: 0,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  });
}

interface RenameInput {
  readonly id: string;
  readonly title: string;
}

export function useRenameConversation(): UseMutationResult<void, ApiError, RenameInput> {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, title }) => renameConversation(id, title),
    onSuccess: () =>
      queryClient.invalidateQueries({ queryKey: assistantKeys.conversations(), exact: true }),
  });
}

export function useDeleteConversation(): UseMutationResult<void, ApiError, string> {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: deleteConversation,
    onSuccess: () =>
      queryClient.invalidateQueries({ queryKey: assistantKeys.conversations(), exact: true }),
  });
}
