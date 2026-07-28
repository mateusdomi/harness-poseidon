import { useCallback, useEffect, useMemo } from 'react';
import { useNavigate, useParams } from 'react-router-dom';

import type { Conversation, Ulid } from '@/api';
import {
  isConversationSelectionCurrent,
  useConversationPreferencesStore,
} from '@/stores/conversation-preferences-store';

export interface ActiveConversationResult {
  conversation: Conversation | null;
  selectConversation: (conversationId: Ulid, options?: { replace?: boolean }) => void;
  requestedConversationUnavailable: boolean;
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
  const navigate = useNavigate();
  const { conversationId: routeConversationId } = useParams<{ conversationId?: string }>();
  const selections = useConversationPreferencesStore(
    (state) => state.selectionsByProfileAndProject,
  );
  const remember = useConversationPreferencesStore((state) => state.selectConversation);
  const clear = useConversationPreferencesStore((state) => state.clearConversation);

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
  const principalConversation =
    conversations.find((conversation) => conversation.state === 'active') ?? null;
  const requestedConversationUnavailable =
    !loading && routeConversationId !== undefined && routeConversation === undefined;
  const conversation = requestedConversationUnavailable
    ? null
    : (routeConversation ?? storedConversation ?? principalConversation);

  const selectConversation = useCallback(
    (nextConversationId: Ulid, options?: { replace?: boolean }) => {
      if (!profileId || !projectId) return;
      const target = conversations.find(
        (candidate) =>
          candidate.id === nextConversationId && candidate.projectId === projectId,
      );
      if (!target) return;
      remember(profileId, projectId, target.id);
      navigate(`/chat/${target.id}`, { replace: options?.replace ?? false });
    },
    [conversations, navigate, profileId, projectId, remember],
  );

  useEffect(() => {
    if (loading || !profileId || !projectId) return;
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
    remember,
    routeConversationId,
    requestedConversationUnavailable,
    storedConversation,
    storedSelection,
  ]);

  return useMemo(
    () => ({ conversation, selectConversation, requestedConversationUnavailable }),
    [conversation, requestedConversationUnavailable, selectConversation],
  );
}
