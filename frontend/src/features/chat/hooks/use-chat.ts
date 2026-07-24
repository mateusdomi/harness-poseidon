import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import {
  streams,
  type ChatTurnEffort,
  type CreateInputMap,
  type Document,
  type Message,
  type Task,
  type Ulid,
} from '@/api';
import { useApi } from '@/app/api-context';
import { reduceChatTurn, IDLE_TURN, type TurnStream } from '@/features/chat/lib/chat-derive';
import { useRealtimeStream } from '@/features/shared/hooks/use-realtime-stream';

export const chatKeys = {
  conversations: (projectId: Ulid) => ['chat', 'conversations', projectId] as const,
  messages: (conversationId: Ulid) => ['chat', 'messages', conversationId] as const,
  models: ['chat', 'models'] as const,
  tasks: (projectId: Ulid) => ['chat', 'tasks', projectId] as const,
  documents: (projectId: Ulid) => ['chat', 'documents', projectId] as const,
};

export function useConversations(projectId: Ulid | null) {
  const api = useApi();
  return useQuery({
    queryKey: chatKeys.conversations(projectId ?? 'none'),
    queryFn: async () =>
      (await api.list('conversations', { filter: { projectId: projectId! } })).items.sort(
        (a, b) => (b.lastMessageAt ?? b.createdAt).localeCompare(a.lastMessageAt ?? a.createdAt),
      ),
    enabled: projectId !== null,
  });
}

export function useMessages(conversationId: Ulid | null) {
  const api = useApi();
  return useQuery({
    queryKey: chatKeys.messages(conversationId ?? 'none'),
    queryFn: async (): Promise<Message[]> =>
      (await api.list('messages', { filter: { conversationId: conversationId! } })).items.sort(
        (a, b) => a.createdAt.localeCompare(b.createdAt),
      ),
    enabled: conversationId !== null,
  });
}

/** Modelos habilitados para o seletor da barra de composição. */
export function useChatModels() {
  const api = useApi();
  return useQuery({
    queryKey: chatKeys.models,
    queryFn: async () => (await api.list('models')).items.filter((model) => model.enabled),
  });
}

/** Tarefas + documentos + agentes do projeto: referências e autoria. */
export function useChatReferences(projectId: Ulid | null) {
  const api = useApi();
  const tasksQuery = useQuery({
    queryKey: chatKeys.tasks(projectId ?? 'none'),
    queryFn: async (): Promise<Task[]> =>
      (await api.list('tasks', { filter: { projectId: projectId! } })).items,
    enabled: projectId !== null,
  });
  const documentsQuery = useQuery({
    queryKey: chatKeys.documents(projectId ?? 'none'),
    queryFn: async (): Promise<Document[]> =>
      (await api.list('documents', { filter: { projectId: projectId! } })).items,
    enabled: projectId !== null,
  });
  const agentsQuery = useQuery({
    queryKey: ['chat', 'agents', projectId ?? 'none'] as const,
    queryFn: async () => (await api.list('agents', { filter: { projectId: projectId! } })).items,
    enabled: projectId !== null,
  });
  return {
    tasks: tasksQuery.data ?? [],
    documents: documentsQuery.data ?? [],
    agents: agentsQuery.data ?? [],
  };
}

export function useCreateConversation() {
  const api = useApi();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: CreateInputMap['conversations']) => api.create('conversations', input),
    onSuccess: (conversation) => {
      void queryClient.invalidateQueries({
        queryKey: chatKeys.conversations(conversation.projectId),
      });
    },
  });
}

/** Turno enviado com a seleção explícita de modelo/esforço (§16.7). */
export interface SendTurnInput {
  content: string;
  /** `''`/undefined = default do agente; caso contrário força o modelo. */
  modelId?: string;
  effort?: ChatTurnEffort;
}

export function useSendMessage(conversationId: Ulid | null) {
  const api = useApi();
  return useMutation({
    mutationFn: ({ content, modelId, effort }: SendTurnInput) =>
      api.startChatTurn(conversationId!, {
        content,
        // Só envia o modelo quando o usuário escolheu um explicitamente.
        ...(modelId ? { modelId } : {}),
        ...(effort ? { effort } : {}),
      }),
  });
}

/** Eventos do stream da conversa que atualizam o turno/ mensagens. */
const CHAT_EVENT_TYPES = [
  'chat.turnStarted',
  'chat.turnChunk',
  'chat.turnCompleted',
  'chief.turnStateChanged',
  'message.appended',
] as const;

/**
 * Turno do chefe em tempo real: acumula chunks (append incremental) e
 * invalida as mensagens quando o turno fecha (`message.appended` /
 * `chat.turnCompleted`). Dedupe/lacuna ficam no useRealtimeStream.
 */
export function useChatTurnStream(conversationId: Ulid | null): TurnStream {
  const [turn, setTurn] = useState<TurnStream>(IDLE_TURN);

  useRealtimeStream(conversationId === null ? null : streams.conversation(conversationId), {
    types: CHAT_EVENT_TYPES,
    onEvent: (event) => setTurn((prev) => reduceChatTurn(prev, event)),
    invalidateEvents: ['chat.turnCompleted', 'message.appended'],
    invalidate: conversationId ? [chatKeys.messages(conversationId)] : [],
  });

  return turn;
}
