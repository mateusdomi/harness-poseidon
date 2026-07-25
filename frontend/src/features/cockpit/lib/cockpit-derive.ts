import type {
  Agent,
  AgentState,
  AuditEvent,
  Budget,
  Gate,
  Phase,
  Progress,
  Task,
  TaskState,
} from '@/api';
import { AGENT_STATES, TASK_STATES } from '@/api';

/**
 * Derivações puras do cockpit — sem React, sem i18n (labels são chaves).
 * Regra de domínio: as TRÊS trilhas (executado/validado/aprovado) são
 * sempre apresentadas separadas; NUNCA somar ou fundir trilhas.
 */

/** Média aritmética por trilha (arredondada). Lista vazia → tudo 0. */
export function aggregateProgress(tasks: readonly Task[]): Progress {
  if (tasks.length === 0) return { executed: 0, validated: 0, approved: 0 };
  const sum = tasks.reduce(
    (acc, task) => ({
      executed: acc.executed + task.progress.executed,
      validated: acc.validated + task.progress.validated,
      approved: acc.approved + task.progress.approved,
    }),
    { executed: 0, validated: 0, approved: 0 },
  );
  return {
    executed: Math.round(sum.executed / tasks.length),
    validated: Math.round(sum.validated / tasks.length),
    approved: Math.round(sum.approved / tasks.length),
  };
}

export interface ProgressEvidenceItem {
  numerator: number;
  denominator: number;
  pendingItems: number;
}

export interface ProgressEvidence {
  tracks: Record<keyof Progress, ProgressEvidenceItem>;
  updatedAt: string | null;
}

/**
 * Evidência do percentual agregado. O numerador é a soma dos pontos de
 * progresso observados; o denominador é 100 pontos por tarefa. Isso torna
 * explícito por que trilhas diferentes podem ter o mesmo percentual.
 */
export function progressEvidence(tasks: readonly Task[]): ProgressEvidence {
  const denominator = tasks.length * 100;
  const item = (track: keyof Progress): ProgressEvidenceItem => ({
    numerator: tasks.reduce((sum, task) => sum + task.progress[track], 0),
    denominator,
    pendingItems: tasks.filter((task) => task.progress[track] < 100).length,
  });
  const timestamps = tasks.map((task) => Date.parse(task.updatedAt)).filter(Number.isFinite);

  return {
    tracks: {
      executed: item('executed'),
      validated: item('validated'),
      approved: item('approved'),
    },
    updatedAt:
      timestamps.length > 0 ? new Date(Math.max(...timestamps)).toISOString() : null,
  };
}

/**
 * Colunas do quadro agregadas por fase do workflow (fase → domínio de
 * colunas). As chaves são os NOMES das fases vindos do template
 * (dado, não texto de UI). Fases fora do mapa agregam todas as colunas.
 */
export const PHASE_TASK_STATES: Record<string, readonly TaskState[]> = {
  Planejamento: ['backlog', 'ready'],
  Execução: ['development'],
  Validação: ['review', 'corrections', 'testsGates'],
  Publicação: ['done'],
};

/** Tarefas agregadas no progresso da fase (todas, se a fase não tem mapa). */
export function tasksOfPhase(tasks: readonly Task[], phase: Phase | null): Task[] {
  if (!phase) return [];
  const states = PHASE_TASK_STATES[phase.name];
  if (!states) return [...tasks];
  return tasks.filter((task) => states.includes(task.state));
}

/** Fase "atual" do run: a ativa, senão a primeira pendente na ordem. */
export function currentPhase(phases: readonly Phase[]): Phase | null {
  const ordered = [...phases].sort((a, b) => a.order - b.order);
  return ordered.find((p) => p.state === 'active') ?? ordered.find((p) => p.state === 'pending') ?? null;
}

/** Gate associado à fase (o primeiro pendente dela, senão qualquer um dela). */
export function gateOfPhase(gates: readonly Gate[], phase: Phase | null): Gate | null {
  if (!phase) return null;
  const ofPhase = gates.filter((g) => g.phaseId === phase.id);
  return ofPhase.find((g) => g.state === 'pending') ?? ofPhase[0] ?? null;
}

/** Contadores por coluna do quadro (todas as 8 colunas sempre presentes). */
export function countTasksByState(tasks: readonly Task[]): Record<TaskState, number> {
  const counts = Object.fromEntries(TASK_STATES.map((s) => [s, 0])) as Record<TaskState, number>;
  for (const task of tasks) counts[task.state] += 1;
  return counts;
}

/** Contadores por estado de agente (todos os estados sempre presentes). */
export function countAgentsByState(agents: readonly Agent[]): Record<AgentState, number> {
  const counts = Object.fromEntries(AGENT_STATES.map((s) => [s, 0])) as Record<AgentState, number>;
  for (const agent of agents) counts[agent.state] += 1;
  return counts;
}

/** Estados de agente que representam capacidade produtiva parada. */
const PROBLEM_AGENT_STATES: readonly AgentState[] = ['error', 'outOfQuota'];

export interface FactoryAgentMetrics {
  /** Prontos para produzir (não em erro/sem cota). */
  online: number;
  /** Capacidade parada (erro ou sem cota). */
  problems: number;
  /** Tarefas já entregues pela equipe (acumulado das métricas dos agentes). */
  tasksDone: number;
  /** Tarefas ainda não concluídas (fila de produção). */
  tasksTodo: number;
  /** Agentes produzindo agora (estado `working`). */
  working: Agent[];
  /** Agentes que precisam de atenção (erro/sem cota). */
  attention: Agent[];
}

/**
 * Métricas da "fábrica de agentes" — visão de dono. Combina o estado
 * operacional dos agentes (capacidade) com a fila de tarefas do projeto
 * (`taskCounts`) para responder "quanta produção há e quanto falta".
 */
export function factoryAgentMetrics(
  agents: readonly Agent[],
  taskCounts: Record<TaskState, number>,
): FactoryAgentMetrics {
  const problems = agents.filter((a) => PROBLEM_AGENT_STATES.includes(a.state));
  const tasksDone = agents.reduce((sum, a) => sum + a.metrics.tasksCompleted, 0);
  const total = TASK_STATES.reduce((sum, s) => sum + taskCounts[s], 0);
  return {
    online: agents.length - problems.length,
    problems: problems.length,
    tasksDone,
    tasksTodo: total - taskCounts.done,
    working: agents.filter((a) => a.state === 'working'),
    attention: [...problems],
  };
}

export type BudgetSeverity = 'ok' | 'warning' | 'critical';

/** Percentual de uso do budget (0–100+); `null` quando o limite é 0. */
export function budgetUsagePct(budget: Budget): number | null {
  if (budget.limitUsd <= 0) return null;
  return Math.round((budget.spentUsd / budget.limitUsd) * 100);
}

/**
 * Severidade do budget: `critical` no/alem de 100%, `warning` a partir do
 * limiar de alerta configurado (`alertThresholdPct`).
 */
export function budgetSeverity(budget: Budget): BudgetSeverity {
  const pct = budgetUsagePct(budget);
  if (pct === null) return 'ok';
  if (pct >= 100) return 'critical';
  if (pct >= budget.alertThresholdPct) return 'warning';
  return 'ok';
}

export type NextActionKey =
  | 'resolveApprovals'
  | 'unblockTasks'
  | 'recoverAgents'
  | 'reviewQuotas'
  | 'reviewPhase';

/* ---- Atividade recente: recorte por período (D-070) ---- */

/** Períodos do feed de atividade (padrão: últimas 24 horas). */
export const ACTIVITY_PERIODS = ['24h', '3d', '7d'] as const;
export type ActivityPeriod = (typeof ACTIVITY_PERIODS)[number];

const ACTIVITY_PERIOD_HOURS: Record<ActivityPeriod, number> = {
  '24h': 24,
  '3d': 72,
  '7d': 168,
};

/**
 * Filtra eventos de auditoria pelo período (janela móvel até `now`).
 * Evento exatamente no corte entra (janela inclusiva).
 */
export function filterActivityByPeriod(
  events: readonly AuditEvent[],
  period: ActivityPeriod,
  now: Date,
): AuditEvent[] {
  const cutoff = now.getTime() - ACTIVITY_PERIOD_HOURS[period] * 3_600_000;
  return events.filter((event) => Date.parse(event.occurredAt) >= cutoff);
}

export interface NextActionInput {
  pendingApprovals: number;
  blockedTasks: number;
  errorAgents: number;
  criticalBudgets: number;
}

/**
 * Próxima ação recomendada (regras em ordem de prioridade).
 * Retorna a CHAVE i18n — label e mensagem do chat vivem no catálogo
 * (`cockpit.nextAction.actions.<key>`).
 */
export function recommendNextAction(input: NextActionInput): NextActionKey {
  if (input.pendingApprovals > 0) return 'resolveApprovals';
  if (input.blockedTasks > 0) return 'unblockTasks';
  if (input.errorAgents > 0) return 'recoverAgents';
  if (input.criticalBudgets > 0) return 'reviewQuotas';
  return 'reviewPhase';
}
