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
  /** Heartbeat: ISO da última atividade observada (evento). "Travado" = sem sinal há X. */
  lastActivityAt: string | null;
  /** Início da fase corrente (base do cronômetro de elapsed). */
  activityStartedAt: string | null;
  /** Agente delegado, quando a fase é delegação/execução de agente. */
  agentName: string | null;
  /** Detalhe granular opcional da fase (ex.: nº de demandas delegadas). */
  detail: string | null;
}

export const IDLE_TURN: TurnStream = {
  turnId: null,
  text: '',
  phase: null,
  lastActivityAt: null,
  activityStartedAt: null,
  agentName: null,
  detail: null,
};

/** Terminais do turno: encerram o stream ativo. */
const TERMINAL_STATES: readonly ChiefTurnState[] = ['completed', 'failed', 'blocked'];

/** O turno está ativo enquanto não chega `chat.turnCompleted`. */
export function isTurnActive(turn: TurnStream): boolean {
  return turn.turnId !== null;
}

/** Redutor de eventos do stream da conversa → estado do turno. */
export function reduceChatTurn(prev: TurnStream, event: EventEnvelope): TurnStream {
  switch (event.type) {
    case 'chat.turnStarted':
      return {
        ...IDLE_TURN,
        turnId: event.payload.turnId,
        phase: 'pending',
        lastActivityAt: event.occurredAt,
        activityStartedAt: event.occurredAt,
      };
    case 'chat.turnChunk':
      if (prev.turnId === null || event.payload.turnId !== prev.turnId) return prev;
      // Cada chunk é um sinal de vida: renova o heartbeat.
      return {
        ...prev,
        text: prev.text + event.payload.text,
        phase: 'processing',
        lastActivityAt: event.occurredAt,
      };
    case 'chat.turnCompleted':
      return IDLE_TURN;
    case 'chief.turnStateChanged': {
      if (TERMINAL_STATES.includes(event.payload.state)) return IDLE_TURN;
      const startedAt =
        event.payload.activityStartedAt ??
        // Nova fase → reinicia o cronômetro; mesma fase → preserva o início.
        (event.payload.state === prev.phase ? prev.activityStartedAt : null) ??
        event.occurredAt;
      return {
        ...prev,
        turnId: event.payload.turnId,
        phase: event.payload.state,
        lastActivityAt: event.payload.lastActivityAt ?? event.occurredAt,
        activityStartedAt: startedAt,
        agentName: event.payload.agentName ?? prev.agentName,
        detail: event.payload.detail ?? null,
      };
    }
    default:
      return prev;
  }
}

/** Limiar (ms) sem heartbeat a partir do qual o turno é sinalizado como possivelmente travado. */
export const STUCK_THRESHOLD_MS = 90_000;

/** Fases longas em que faz sentido exibir o cronômetro de elapsed. */
const TIMED_PHASES: ReadonlySet<ChiefTurnState> = new Set<ChiefTurnState>([
  'reading_context',
  'thinking',
  'planning',
  'delegating',
  'agent_working',
  'awaiting_review',
]);

/** Status derivado do turno para o balão da conversa (tag + cronômetro + travado). */
export interface TurnStatus {
  phase: ChiefTurnState;
  /** Tempo decorrido na fase corrente (ms), quando aplicável. */
  elapsedMs: number | null;
  /** Sem heartbeat além do limiar → possivelmente travado (nunca para fases terminais). */
  stuck: boolean;
  agentName: string | null;
  detail: string | null;
}

/**
 * Deriva o status visível a partir do stream e do relógio atual (`nowMs`).
 * Puro e testável: elapsed vem de `activityStartedAt`; "travado" vem da ausência
 * de heartbeat (`lastActivityAt`) além de {@link STUCK_THRESHOLD_MS}.
 */
export function deriveTurnStatus(turn: TurnStream, nowMs: number): TurnStatus | null {
  if (turn.turnId === null || turn.phase === null) return null;
  const startedMs = turn.activityStartedAt ? Date.parse(turn.activityStartedAt) : null;
  const elapsedMs =
    startedMs !== null && TIMED_PHASES.has(turn.phase) ? Math.max(0, nowMs - startedMs) : null;
  const lastMs = turn.lastActivityAt ? Date.parse(turn.lastActivityAt) : null;
  const stuck = lastMs !== null && nowMs - lastMs > STUCK_THRESHOLD_MS;
  return {
    phase: turn.phase,
    elapsedMs,
    stuck,
    agentName: turn.agentName,
    detail: turn.detail,
  };
}

/** Formata `ms` como cronômetro humano curto (ex.: "12min", "45s"). */
export function formatElapsed(ms: number): string {
  const totalSeconds = Math.floor(ms / 1000);
  if (totalSeconds < 60) return `${totalSeconds}s`;
  const minutes = Math.floor(totalSeconds / 60);
  if (minutes < 60) return `${minutes}min`;
  const hours = Math.floor(minutes / 60);
  return `${hours}h${String(minutes % 60).padStart(2, '0')}`;
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
