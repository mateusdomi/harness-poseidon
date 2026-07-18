import type {
  Account,
  Agent,
  AgentDefinition,
  AgentState,
  Attempt,
  AttemptEvent,
  Budget,
  Model,
  Ulid,
} from '@/api';
import { AGENT_STATES } from '@/api';

/**
 * Derivações puras do orquestrador — sem React, sem i18n (labels são chaves).
 * Toda lógica de saúde/agrupamento/resolução de modelo-conta-cota fica aqui
 * para ser testada isolada dos componentes.
 */

export type ChiefHealth = 'ok' | 'attention' | 'error';

/** Heartbeat acima deste intervalo é considerado parado (atenção). */
export const STALE_HEARTBEAT_MS = 120_000;

/**
 * Saúde do chefe a partir do estado do agente + último heartbeat:
 * erro/cota estourada → erro; heartbeat ausente ou parado → atenção; senão ok.
 */
export function deriveChiefHealth(
  state: AgentState,
  lastHeartbeatAt: string | null,
  now: Date,
): ChiefHealth {
  if (state === 'error' || state === 'outOfQuota') return 'error';
  if (lastHeartbeatAt === null) return 'attention';
  if (now.getTime() - Date.parse(lastHeartbeatAt) > STALE_HEARTBEAT_MS) return 'attention';
  return 'ok';
}

/** Grupo da grade de agentes: um estado com suas instâncias (só não vazios). */
export interface AgentStateGroup {
  state: AgentState;
  agents: Agent[];
}

/**
 * Agrupa agentes por estado na ordem fixa do domínio
 * (working → idle → waiting → error → outOfQuota), omitindo grupos vazios.
 */
export function groupAgentsByState(agents: readonly Agent[]): AgentStateGroup[] {
  return AGENT_STATES.map((state) => ({
    state,
    agents: agents.filter((agent) => agent.state === state),
  })).filter((group) => group.agents.length > 0);
}

/** Tentativas de um agente (qualquer estado). */
export function attemptsOf(attempts: readonly Attempt[], agentId: Ulid): Attempt[] {
  return attempts.filter((attempt) => attempt.agentId === agentId);
}

/** Tentativa em execução do agente (a mais recente, se houver mais de uma). */
export function runningAttemptOf(attempts: readonly Attempt[], agentId: Ulid): Attempt | null {
  const running = attemptsOf(attempts, agentId).filter((attempt) => attempt.state === 'running');
  return running.sort((a, b) => b.startedAt.localeCompare(a.startedAt))[0] ?? null;
}

/** Tentativa mais recente do agente (running tem prioridade, senão a última). */
export function latestAttemptOf(attempts: readonly Attempt[], agentId: Ulid): Attempt | null {
  const running = runningAttemptOf(attempts, agentId);
  if (running) return running;
  const mine = attemptsOf(attempts, agentId);
  return mine.sort((a, b) => b.startedAt.localeCompare(a.startedAt))[0] ?? null;
}

/**
 * Modelo em uso pelo chefe: override da instância (`agent.modelId`) ou o
 * `defaultModelId` da definição. Nulo quando nenhum dos dois existe.
 */
export function resolveChiefModel(
  agent: Agent,
  definition: AgentDefinition | null,
  models: readonly Model[],
): Model | null {
  const modelId = agent.modelId ?? definition?.defaultModelId ?? null;
  if (modelId === null) return null;
  return models.find((model) => model.id === modelId) ?? null;
}

/**
 * Conta em uso pelo modelo: contas do provedor do modelo, preferindo a de
 * estado `active` (a primeira, em ordem de cadastro, como fallback).
 */
export function resolveChiefAccount(
  model: Model | null,
  accounts: readonly Account[],
): Account | null {
  if (model === null) return null;
  const candidates = accounts.filter((account) => account.providerId === model.providerId);
  return candidates.find((account) => account.state === 'active') ?? candidates[0] ?? null;
}

/**
 * Cotas relevantes ao chefe: budget de escopo `project` do projeto e/ou de
 * escopo `account` da conta em uso. Escopo global NÃO entra (é do cockpit).
 */
export function chiefBudgets(
  budgets: readonly Budget[],
  projectId: Ulid,
  accountId: Ulid | null,
): Budget[] {
  return budgets.filter(
    (budget) =>
      (budget.scope === 'project' && budget.scopeId === projectId) ||
      (budget.scope === 'account' && accountId !== null && budget.scopeId === accountId),
  );
}

/** Eventos da tentativa em ordem cronológica (estável por id no empate). */
export function sortAttemptEvents(events: readonly AttemptEvent[]): AttemptEvent[] {
  return [...events].sort(
    (a, b) => a.occurredAt.localeCompare(b.occurredAt) || a.id.localeCompare(b.id),
  );
}

export type AttemptEventKindFilter = AttemptEvent['kind'] | '';

/**
 * Filtro do log estruturado: por tipo (kind) e busca textual
 * case-insensitive no conteúdo. O contrato AttemptEvent NÃO tem nível —
 * não inventar campo (ver resumo da feature).
 */
export function filterAttemptEvents(
  events: readonly AttemptEvent[],
  kind: AttemptEventKindFilter,
  query: string,
): AttemptEvent[] {
  const needle = query.trim().toLowerCase();
  return events.filter((event) => {
    if (kind !== '' && event.kind !== kind) return false;
    if (needle !== '' && !event.content.toLowerCase().includes(needle)) return false;
    return true;
  });
}
