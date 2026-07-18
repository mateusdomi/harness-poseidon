import type {
  Agent,
  AgentDefinition,
  Attempt,
  AuditEvent,
  Model,
  Project,
  Skill,
  Task,
  Tool,
  Ulid,
} from '@/api';

/**
 * Derivações da tela de agentes — funções puras (sem React/i18n).
 * NENHUM campo é inventado: tudo sai dos contratos de `agents`,
 * `delivery` (tasks/attempts), `system` (audit-events) e `providers` (models).
 */

/** Equipe do projeto: chefe (instância apontada por `chiefAgentId`) + especialistas. */
export interface AgentTeam {
  chief: Agent | null;
  specialists: Agent[];
}

/**
 * Monta o organograma do projeto: o chefe é a instância cujo id é
 * `project.chiefAgentId`; os demais agentes alocados no projeto são
 * especialistas (ordenados por nome para um layout estável).
 */
export function teamOfProject(project: Project, agents: Agent[]): AgentTeam {
  const allocated = agents.filter((agent) => agent.projectId === project.id);
  const chief = allocated.find((agent) => agent.id === project.chiefAgentId) ?? null;
  const specialists = allocated
    .filter((agent) => agent.id !== chief?.id)
    .sort((a, b) => a.name.localeCompare(b.name));
  return { chief, specialists };
}

/** Definição (tipo) da instância; `null` se o cadastro não existir. */
export function definitionOf(
  agent: Agent,
  definitions: AgentDefinition[],
): AgentDefinition | null {
  return definitions.find((definition) => definition.id === agent.definitionId) ?? null;
}

/** Métricas objetivas exibidas no card do agente. */
export interface DerivedAgentMetrics {
  /** Total acumulado de tarefas concluídas (`agent.metrics.tasksCompleted`). */
  tasksCompleted: number;
  /** Tarefas do agente (`assigneeAgentId`) atualmente na coluna `done`. */
  approvedInReview: number;
  /** Tarefas do agente em `corrections` + attempts `failed` do agente. */
  rework: number;
}

/**
 * Deriva as métricas do card: concluídas vêm do acumulado do contrato;
 * aprovadas em revisão e retrabalho são derivados de tarefas e attempts.
 */
export function deriveAgentMetrics(
  agent: Agent,
  tasks: Task[],
  attempts: Attempt[],
): DerivedAgentMetrics {
  const assigned = tasks.filter((task) => task.assigneeAgentId === agent.id);
  return {
    tasksCompleted: agent.metrics.tasksCompleted,
    approvedInReview: assigned.filter((task) => task.state === 'done').length,
    rework:
      assigned.filter((task) => task.state === 'corrections').length +
      attempts.filter((attempt) => attempt.agentId === agent.id && attempt.state === 'failed')
        .length,
  };
}

/** Skills da definição, resolvidas na ordem declarada (ids sem cadastro são ignorados). */
export function agentSkills(definition: AgentDefinition | null, skills: Skill[]): Skill[] {
  if (!definition) return [];
  return definition.skillIds
    .map((id) => skills.find((skill) => skill.id === id))
    .filter((skill): skill is Skill => skill !== undefined);
}

/** Ferramentas permitidas da definição, resolvidas na ordem declarada. */
export function agentTools(definition: AgentDefinition | null, tools: Tool[]): Tool[] {
  if (!definition) return [];
  return definition.toolIds
    .map((id) => tools.find((tool) => tool.id === id))
    .filter((tool): tool is Tool => tool !== undefined);
}

/**
 * Modelos compatíveis = modelos habilitados. O destaque de padrão/override
 * é derivado por id na UI ({@link effectiveModelId}).
 */
export function compatibleModels(models: Model[]): Model[] {
  return models.filter((model) => model.enabled);
}

/**
 * Modelo efetivo da instância: override da passagem de bastão
 * (`agent.modelId`) ou `defaultModelId` da definição.
 */
export function effectiveModelId(
  agent: Agent,
  definition: AgentDefinition | null,
): Ulid | null {
  return agent.modelId ?? definition?.defaultModelId ?? null;
}

/** Entrada unificada do histórico do agente (auditoria + tentativas). */
export type AgentHistoryEntry =
  | { kind: 'audit'; event: AuditEvent }
  | { kind: 'attempt'; attempt: Attempt };

function entryDate(entry: AgentHistoryEntry): string {
  return entry.kind === 'audit' ? entry.event.occurredAt : entry.attempt.startedAt;
}

/**
 * Histórico recente do agente: eventos de auditoria em que ele é ator ou
 * alvo + suas tentativas, ordenados do mais recente para o mais antigo.
 */
export function agentHistory(
  agentId: Ulid,
  auditEvents: AuditEvent[],
  attempts: Attempt[],
  limit = 10,
): AgentHistoryEntry[] {
  const entries: AgentHistoryEntry[] = [
    ...auditEvents
      .filter((event) => event.actorId === agentId || event.targetId === agentId)
      .map((event): AgentHistoryEntry => ({ kind: 'audit', event })),
    ...attempts
      .filter((attempt) => attempt.agentId === agentId)
      .map((attempt): AgentHistoryEntry => ({ kind: 'attempt', attempt })),
  ];
  return entries
    .sort((a, b) => Date.parse(entryDate(b)) - Date.parse(entryDate(a)))
    .slice(0, limit);
}
