import { useCallback, useEffect, useMemo, useRef } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useParams } from 'react-router-dom';

import type { Conversation, Ulid } from '@/api';
import { useApi } from '@/app/api-context';
import {
  isConversationSelectionCurrent,
  useConversationPreferencesStore,
} from '@/stores/conversation-preferences-store';

export interface ActiveConversationResult {
  conversation: Conversation | null;
  selectConversation: (
    conversation: Ulid | Conversation,
    options?: { replace?: boolean },
  ) => void;
  requestedConversationUnavailable: boolean;
  restoring: boolean;
}

/**
 * Resolve a conversa canônica do projeto, persiste a última seleção por
 * usuário/projeto e mantém a rota `/chat/{conversationId}` sincronizada.
 * Nunca cria conversa como efeito da navegação.
 */
export function useActiveConversation(
  profileId: Ulid | null,
  projectId: Ulid | null,
  conversations: Conversation[],
  loading: boolean,
): ActiveConversationResult {
  const api = useApi();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const { conversationId: routeConversationId } = useParams<{ conversationId?: string }>();
  const selections = useConversationPreferencesStore(
    (state) => state.selectionsByProfileAndProject,
  );
  const remember = useConversationPreferencesStore((state) => state.selectConversation);
  const clear = useConversationPreferencesStore((state) => state.clearConversation);
  const persistenceQueues = useRef(
    new Map<
      Ulid,
      {
        pending: Ulid | null;
        running: boolean;
      }
    >(),
  );
  const persistSelection = useCallback(
    (targetProjectId: Ulid, conversationId: Ulid, queryKey: readonly unknown[]) => {
      // Atualização otimista impede o efeito de emitir um PUT duplicado.
      queryClient.setQueryData(queryKey, conversationId);
      const queue = persistenceQueues.current.get(targetProjectId) ?? {
        pending: null,
        running: false,
      };
      queue.pending = conversationId;
      persistenceQueues.current.set(targetProjectId, queue);
      if (queue.running) return;
      queue.running = true;

      void (async () => {
        try
        {
          while (queue.pending !== null)
          {
            const next = queue.pending;
            queue.pending = null;
            try
            {
              await api.rememberActiveConversation(targetProjectId, next);
            }
            catch
            {
              // O cache local continua sendo fallback offline. Se houver uma intenção mais
              // recente, ela ainda será gravada na próxima volta do laço.
            }
          }
        }
        finally
        {
          queue.running = false;
        }
      })();
    },
    [api, queryClient],
  );
  const serverSelectionKey = useMemo(
    () =>
      [
        'chat',
        'active-conversation',
        profileId ?? 'none',
        projectId ?? 'none',
      ] as const,
    [profileId, projectId],
  );
  const serverSelection = useQuery({
    queryKey: serverSelectionKey,
    queryFn: () => api.recallActiveConversation(projectId!),
    enabled: profileId !== null && projectId !== null,
    staleTime: Number.POSITIVE_INFINITY,
  });

  const routeConversation = routeConversationId
    ? conversations.find((conversation) => conversation.id === routeConversationId)
    : undefined;
  const storedSelection =
    profileId && projectId ? selections[profileId]?.[projectId] : undefined;
  const storedConversation = isConversationSelectionCurrent(storedSelection)
    ? conversations.find(
        (conversation) => conversation.id === storedSelection.conversationId,
      )
    : undefined;
  const serverConversation = serverSelection.data
    ? conversations.find(
        (conversation) =>
          conversation.id === serverSelection.data && conversation.state === 'active',
      )
    : undefined;
  const principalConversation =
    conversations.find((conversation) => conversation.state === 'active') ?? null;
  const requestedConversationUnavailable =
    !loading && routeConversationId !== undefined && routeConversation === undefined;
  const conversation = requestedConversationUnavailable
    ? null
    : (routeConversation ?? serverConversation ?? storedConversation ?? principalConversation);

  const selectConversation = useCallback(
    (nextConversation: Ulid | Conversation, options?: { replace?: boolean }) => {
      if (!profileId || !projectId) return;
      const target =
        typeof nextConversation === 'string'
          ? conversations.find(
              (candidate) =>
                candidate.id === nextConversation && candidate.projectId === projectId,
            )
          : nextConversation.projectId === projectId
            ? nextConversation
            : undefined;
      if (!target) return;
      remember(profileId, projectId, target.id);
      persistSelection(projectId, target.id, serverSelectionKey);
      navigate(`/chat/${target.id}`, { replace: options?.replace ?? false });
    },
    [
      conversations,
      navigate,
      profileId,
      projectId,
      persistSelection,
      remember,
      serverSelectionKey,
    ],
  );

  useEffect(() => {
    if (loading || serverSelection.isLoading || !profileId || !projectId) return;
    if (requestedConversationUnavailable) return;

    if (storedSelection && !storedConversation) {
      clear(profileId, projectId);
    }
    if (!conversation) {
      if (routeConversationId !== undefined) navigate('/chat', { replace: true });
      return;
    }

    if (
      storedSelection?.conversationId !== conversation.id ||
      !isConversationSelectionCurrent(storedSelection)
    ) {
      remember(profileId, projectId, conversation.id);
    }
    if (serverSelection.data !== conversation.id) {
      persistSelection(projectId, conversation.id, serverSelectionKey);
    }
    if (routeConversationId !== conversation.id) {
      navigate(`/chat/${conversation.id}`, { replace: true });
    }
  }, [
    clear,
    conversation,
    loading,
    navigate,
    profileId,
    projectId,
    persistSelection,
    remember,
    routeConversationId,
    requestedConversationUnavailable,
    serverSelection.data,
    serverSelection.isLoading,
    serverSelectionKey,
    storedConversation,
    storedSelection,
  ]);

  return useMemo(
    () => ({
      conversation,
      selectConversation,
      requestedConversationUnavailable,
      restoring: serverSelection.isLoading,
    }),
    [
      conversation,
      requestedConversationUnavailable,
      selectConversation,
      serverSelection.isLoading,
    ],
  );
}
