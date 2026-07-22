import type { ChiefTurnState, Document, EventEnvelope, Task } from '@/api';

/**
 * Derivações puras do chat — streaming do turno, referências cruzadas e
 * ações rápidas. Sem React; labels/mensagens são CHAVES i18n.
 */

/** Estado do turno do chefe em andamento (acumulado dos eventos). */
export interface TurnStream {
  turnId: string | null;
  /** Texto parcial acumulado dos chunks (append incremental). */
  text: string;
  /** Fase de orquestração reportada pelo chefe, quando emitida. */
  phase: ChiefTurnState | null;
}

export const IDLE_TURN: TurnStream = { turnId: null, text: '', phase: null };

/** O turno está ativo enquanto não chega `chat.turnCompleted`. */
export function isTurnActive(turn: TurnStream): boolean {
  return turn.turnId !== null;
}

/** Redutor de eventos do stream da conversa → estado do turno. */
export function reduceChatTurn(prev: TurnStream, event: EventEnvelope): TurnStream {
  switch (event.type) {
    case 'chat.turnStarted':
      return { turnId: event.payload.turnId, text: '', phase: 'pending' };
    case 'chat.turnChunk':
      if (prev.turnId === null || event.payload.turnId !== prev.turnId) return prev;
      return { ...prev, text: prev.text + event.payload.text, phase: 'processing' };
    case 'chat.turnCompleted':
      return IDLE_TURN;
    case 'chief.turnStateChanged':
      if (['completed', 'failed', 'blocked'].includes(event.payload.state)) return IDLE_TURN;
      return { ...prev, turnId: event.payload.turnId, phase: event.payload.state };
    default:
      return prev;
  }
}

/** Referência cruzada citada numa mensagem (tarefa ou documento). */
export interface MessageReference {
  kind: 'task' | 'document';
  id: string;
  title: string;
}

/**
 * Extrai citações `"…"` do conteúdo e casa com títulos de tarefas e
 * documentos do projeto (case-insensitive, prefixo tolerado). Cada
 * referência vira um chip navegável na mensagem.
 */
export function extractReferences(
  content: string,
  tasks: readonly Task[],
  documents: readonly Document[],
): MessageReference[] {
  const quoted = [...content.matchAll(/["“](.+?)["”]/g)].map((m) => m[1].trim().toLowerCase());
  if (quoted.length === 0) return [];

  const references: MessageReference[] = [];
  const seen = new Set<string>();
  const push = (kind: MessageReference['kind'], id: string, title: string) => {
    const key = `${kind}:${id}`;
    if (!seen.has(key)) {
      seen.add(key);
      references.push({ kind, id, title });
    }
  };

  for (const task of tasks) {
    const title = task.title.toLowerCase();
    if (quoted.some((q) => title.includes(q) || q.includes(title))) {
      push('task', task.id, task.title);
    }
  }
  for (const document of documents) {
    const title = document.title.toLowerCase();
    if (quoted.some((q) => title.includes(q) || q.includes(title))) {
      push('document', document.id, document.title);
    }
  }
  return references;
}

export type QuickActionKey =
  | 'summarizeProgress'
  | 'blockedStatus'
  | 'approvalStatus'
  | 'planNewDemand';

export interface QuickActionContext {
  blockedTasks: number;
  pendingApprovals: number;
}

/**
 * Ações rápidas derivadas do contexto do projeto (chefe propõe o próximo
 * passo). Retorna chaves i18n (`chat.quickActions.actions.<key>`).
 */
export function deriveQuickActions(context: QuickActionContext): QuickActionKey[] {
  const actions: QuickActionKey[] = ['summarizeProgress'];
  if (context.blockedTasks > 0) actions.push('blockedStatus');
  if (context.pendingApprovals > 0) actions.push('approvalStatus');
  actions.push('planNewDemand');
  return actions;
}
