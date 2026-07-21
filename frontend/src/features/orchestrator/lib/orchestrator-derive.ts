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

/**
 * Prontidão operacional do chefe (§15). Substitui a impressão de "pronto" por
 * um estado explícito e honesto, derivado só de recursos reais.
 */
export type ChiefReadiness =
  | 'notConfigured'
  | 'awaitingProvider'
  | 'awaitingWorkflow'
  | 'ready'
  | 'running'
  | 'degraded';

/** CTA correspondente a cada estado de prontidão (null = nada a fazer). */
export type ChiefReadinessAction =
  | 'configureProvider'
  | 'chooseModel'
  | 'linkWorkflow'
  | 'reviewAgents'
  | null;

export interface ChiefReadinessInput {
  /** Modelo efetivamente resolvido para o chefe (null = nenhum). */
  model: Model | null;
  /** Conta ativa do provedor do modelo (null = nenhuma). */
  account: Account | null;
  /** Workflow vinculado ao projeto. */
  hasWorkflow: boolean;
  /** Estado do agente chefe. */
  agentState: AgentState;
  /** Há tentativa em execução agora. */
  isRunning: boolean;
  /** Saúde derivada do heartbeat/estado. */
  health: ChiefHealth;
}

/**
 * Ordem de precedência (fail-closed — degradação e falta de pré-requisito
 * vencem qualquer aparência de prontidão):
 * degradado → em execução → sem modelo/conta → sem workflow → pronto.
 */
export function deriveChiefReadiness(input: ChiefReadinessInput): ChiefReadiness {
  if (input.health === 'error' || input.agentState === 'error') return 'degraded';
  if (input.agentState === 'outOfQuota') return 'degraded';
  if (input.model === null && input.account === null && !input.hasWorkflow) {
    return 'notConfigured';
  }
  if (input.model === null || input.account === null) return 'awaitingProvider';
  if (!input.hasWorkflow) return 'awaitingWorkflow';
  if (input.isRunning) return 'running';
  return 'ready';
}

/** CTA única e correta para cada estado de prontidão. */
export function readinessAction(readiness: ChiefReadiness): ChiefReadinessAction {
  switch (readiness) {
    case 'notConfigured':
    case 'awaitingProvider':
      return 'configureProvider';
    case 'awaitingWorkflow':
      return 'linkWorkflow';
    case 'degraded':
      return 'reviewAgents';
    case 'ready':
    case 'running':
      return null;
  }
}

/**
 * Origem do vínculo modelo/conta — evita apresentar um padrão de definição
 * como se fosse um vínculo real da instância (§15/§17).
 * - `instance`: o agente tem `modelId` próprio (vínculo real em uso).
 * - `definitionDefault`: veio do `defaultModelId` da definição (ainda não
 *   exercido pela instância) → a UI deve rotular como "binding pendente".
 * - `none`: não há vínculo algum.
 */
export type BindingSource = 'instance' | 'definitionDefault' | 'none';

export function resolveModelBindingSource(
  agent: Agent,
  definition: AgentDefinition | null,
): BindingSource {
  if (agent.modelId) return 'instance';
  if (definition?.defaultModelId) return 'definitionDefault';
  return 'none';
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
