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

/** Grupo de especialistas por time da definição (`team: null` = grupo "geral"). */
export interface SpecialistTeamGroup {
  /** Nome do time; `null` quando a definição não tem time (grupo "geral"). */
  team: string | null;
  agents: Agent[];
}

/**
 * Agrupa especialistas pelo `team` da definição correspondente. Ordem
 * estável: times em ordem alfabética, grupo "geral" (sem time) por último.
 * Dentro do grupo, preserva a ordem recebida (já ordenada por nome em
 * {@link teamOfProject}). Definição ausente conta como "sem time".
 */
export function groupSpecialistsByTeam(
  specialists: Agent[],
  definitions: AgentDefinition[],
): SpecialistTeamGroup[] {
  const groups = new Map<string | null, Agent[]>();
  for (const agent of specialists) {
    const team = definitionOf(agent, definitions)?.team ?? null;
    const bucket = groups.get(team);
    if (bucket) bucket.push(agent);
    else groups.set(team, [agent]);
  }
  return [...groups.entries()]
    .sort(([a], [b]) => {
      if (a === null) return 1;
      if (b === null) return -1;
      return a.localeCompare(b);
    })
    .map(([team, agents]) => ({ team, agents }));
}

/** Valor do filtro de time: `all` (todos), `none` (sem time → "geral") ou um nome de time. */
export type TeamFilter = 'all' | 'none' | (string & {});

/**
 * Times distintos presentes entre os especialistas (para as opções do
 * filtro): nomes em ordem alfabética + `hasGeneral` se algum não tem time.
 */
export function distinctTeams(
  specialists: Agent[],
  definitions: AgentDefinition[],
): { teams: string[]; hasGeneral: boolean } {
  const teams = new Set<string>();
  let hasGeneral = false;
  for (const agent of specialists) {
    const team = definitionOf(agent, definitions)?.team ?? null;
    if (team === null) hasGeneral = true;
    else teams.add(team);
  }
  return { teams: [...teams].sort((a, b) => a.localeCompare(b)), hasGeneral };
}

/**
 * Filtra especialistas por estado da instância e/ou time da definição.
 * `state: 'all'` e `team: 'all'` não restringem; `team: 'none'` filtra o
 * grupo "geral" (definição sem time ou ausente).
 */
export function filterSpecialists(
  specialists: Agent[],
  definitions: AgentDefinition[],
  state: Agent['state'] | 'all',
  team: TeamFilter,
): Agent[] {
  return specialists.filter((agent) => {
    if (state !== 'all' && agent.state !== state) return false;
    if (team !== 'all') {
      const agentTeam = definitionOf(agent, definitions)?.team ?? null;
      if (team === 'none' ? agentTeam !== null : agentTeam !== team) return false;
    }
    return true;
  });
}

/** Rota de modelo da instância, resolvida no catálogo de modelos. */
export interface AgentModelRoute {
  /** Modelo efetivamente em uso (`null` se não resolvido no catálogo). */
  current: Model | null;
  /** Origem da decisão: override humano (`agent.modelId`) ou padrão da definição. */
  source: 'override' | 'default';
  /** Modelo padrão da definição (`null` se não resolvido no catálogo). */
  defaultModel: Model | null;
  /** Fallbacks da definição, resolvidos na ordem declarada (ids sem cadastro são ignorados). */
  fallbacks: Model[];
}

/**
 * Modelo/rota da instância: modelo em uso ({@link effectiveModelId}),
 * origem da decisão, padrão da definição e fallbacks — tudo resolvido no
 * catálogo completo (o override pode apontar para modelo desabilitado).
 * O contrato NÃO registra o motivo do roteamento — nada é inventado aqui.
 */
export function agentModelRoute(
  agent: Agent,
  definition: AgentDefinition | null,
  models: Model[],
): AgentModelRoute {
  const byId = (id: Ulid | null | undefined): Model | null =>
    models.find((model) => model.id === id) ?? null;
  return {
    current: byId(effectiveModelId(agent, definition)),
    source: agent.modelId !== null ? 'override' : 'default',
    defaultModel: byId(definition?.defaultModelId),
    fallbacks: (agent.fallbackModelIds ?? definition?.fallbackModelIds ?? [])
      .map((id) => byId(id))
      .filter((model): model is Model => model !== null),
  };
}

/** Métricas objetivas exibidas no card do agente. */
export interface DerivedAgentMetrics {
  /** Total acumulado de entregas concluídas (`agent.metrics.tasksCompleted`). */
  tasksCompleted: number;
  /** Itens atribuídos ao agente (`assigneeAgentId`) atualmente em `done`. */
  approvedInReview: number;
  /** Itens do agente em `corrections` + attempts `failed` do agente. */
  rework: number;
}

/**
 * Deriva as métricas do card: concluídas vêm do acumulado do contrato;
 * checks concluídos e ajustes são derivados do read model técnico.
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
