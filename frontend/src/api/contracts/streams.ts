import type { Ulid } from './primitives';

/**
 * Nomes de streams do hub `/hubs/events` — mesma convenção do backend.
 * Assinatura por stream; `sequence` é crescente dentro de cada stream.
 */
export const streams = {
  /** Eventos do projeto: tarefas, demandas, documentos, protótipos, gates, quota. */
  project: (projectId: Ulid) => `project:${projectId}`,
  /** Turnos e mensagens de uma conversa com o chefe. */
  conversation: (conversationId: Ulid) => `conversation:${conversationId}`,
  /** Notificações e eventos pessoais do perfil. */
  profile: (profileId: Ulid) => `profile:${profileId}`,
  /** Detalhe de uma tarefa (progresso, attempts). */
  task: (taskId: Ulid) => `task:${taskId}`,
  /** Logs/heartbeat de uma tentativa. */
  attempt: (attemptId: Ulid) => `attempt:${attemptId}`,
  /** Eventos de um workflow run (fases, gates, logs). */
  run: (runId: Ulid) => `run:${runId}`,
  /** Eventos globais: agentes, ferramentas, auditoria, licença. */
  global: () => 'global',
} as const;
