import type {
  Agent,
  Approval,
  Attempt,
  AuditActorKind,
  AuditEvent,
  Demand,
  Document,
  Model,
  Profile,
  Project,
  Solicitation,
  Task,
  Tool,
  Ulid,
  Workflow,
} from '@/api';

/**
 * Derivações da trilha de auditoria (GOVERNANCE): ordenação, busca textual,
 * filtros correlacionados e resolução de ator/alvo. Funções puras — sem
 * React, i18n ou API — para teste unitário direto.
 *
 * REGRA DE CORRELAÇÃO (documentada e testada): o contrato de `AuditEvent`
 * só tem `actorId`/`targetId` (ULIDs soltos) e `targetType` (string livre).
 * Um filtro por entidade casa por `actorId` (filtro de ator) ou por
 * `targetId` (demais filtros), resolvendo o alvo conforme `targetType`:
 * - projeto: task/approval/document/demand/solicitation → `projectId` da
 *   entidade; attempt → `projectId` da tarefa da attempt; workflow/agent →
 *   `projectId` próprio. Sem alvo correlacionável, tenta o `projectId` do
 *   agente ATOR. Se nada resolver (ex.: evento de licença sem `targetId`),
 *   o evento NÃO é correlacionável e só aparece com o filtro em "todos".
 * - tarefa: alvo é a própria task, ou attempt (`taskId` da attempt), ou
 *   approval (`taskId` da aprovação).
 * - tentativa/modelo/ferramenta: só casam quando `targetType` corresponde
 *   ('attempt'/'model'/'tool') e o `targetId` é o da entidade — o contrato
 *   não liga audit-event a modelo/ferramenta por outro caminho.
 */

/** Listas de apoio para resolver atores, alvos e correlações. */
export interface AuditCatalog {
  projects: Project[];
  tasks: Task[];
  attempts: Attempt[];
  approvals: Approval[];
  documents: Document[];
  demands: Demand[];
  solicitations: Solicitation[];
  workflows: Workflow[];
  profiles: Profile[];
  agents: Agent[];
  models: Model[];
  tools: Tool[];
}

export interface AuditFilters {
  /** Busca textual livre (ação e detalhe). */
  search: string;
  actorKind: AuditActorKind | '';
  actorId: Ulid | '';
  projectId: Ulid | '';
  taskId: Ulid | '';
  attemptId: Ulid | '';
  modelId: Ulid | '';
  toolId: Ulid | '';
  /** Período em `yyyy-mm-dd` (input date); vazio = sem limite. */
  dateFrom: string;
  dateTo: string;
}

export const EMPTY_AUDIT_FILTERS: AuditFilters = {
  search: '',
  actorKind: '',
  actorId: '',
  projectId: '',
  taskId: '',
  attemptId: '',
  modelId: '',
  toolId: '',
  dateFrom: '',
  dateTo: '',
};

/** Cópia ordenada por `occurredAt` desc (mais recente primeiro). */
export function sortAuditEventsDesc(events: readonly AuditEvent[]): AuditEvent[] {
  return [...events].sort((a, b) => b.occurredAt.localeCompare(a.occurredAt));
}

/**
 * Busca textual livre: case-insensitive, ignorando espaços das pontas,
 * sobre `action` e `detail`. Busca vazia casa tudo.
 */
export function matchesAuditSearch(event: AuditEvent, search: string): boolean {
  const query = search.trim().toLowerCase();
  if (query === '') return true;
  return (
    event.action.toLowerCase().includes(query) ||
    (event.detail?.toLowerCase().includes(query) ?? false)
  );
}

/** Nome de exibição do ator (perfil ou agente); `null` quando não resolve. */
export function resolveActorName(event: AuditEvent, catalog: AuditCatalog): string | null {
  if (event.actorId === null) return null;
  return (
    catalog.profiles.find((profile) => profile.id === event.actorId)?.displayName ??
    catalog.agents.find((agent) => agent.id === event.actorId)?.name ??
    null
  );
}

/**
 * Nome de exibição do alvo conforme `targetType`. Workflow não tem nome
 * próprio: usa o nome do projeto ao qual está vinculado. Attempt usa
 * `#<número>` (o rótulo completo é montado na UI, com i18n). `null`
 * quando o alvo não é correlacionável com as listas do catálogo.
 */
export function resolveTargetName(event: AuditEvent, catalog: AuditCatalog): string | null {
  const { targetType, targetId } = event;
  if (targetId === null) return null;
  switch (targetType) {
    case 'workflow': {
      const workflow = catalog.workflows.find((item) => item.id === targetId);
      return (
        catalog.projects.find((project) => project.id === workflow?.projectId)?.name ?? null
      );
    }
    case 'task':
      return catalog.tasks.find((task) => task.id === targetId)?.title ?? null;
    case 'approval':
      return catalog.approvals.find((approval) => approval.id === targetId)?.title ?? null;
    case 'attempt': {
      const attempt = catalog.attempts.find((item) => item.id === targetId);
      return attempt ? `#${attempt.number}` : null;
    }
    case 'agent':
      return catalog.agents.find((agent) => agent.id === targetId)?.name ?? null;
    case 'document':
      return catalog.documents.find((document) => document.id === targetId)?.title ?? null;
    case 'demand':
      return catalog.demands.find((demand) => demand.id === targetId)?.title ?? null;
    case 'solicitation':
      return catalog.solicitations.find((solicitation) => solicitation.id === targetId)?.title ?? null;
    case 'model':
      return catalog.models.find((model) => model.id === targetId)?.displayName ?? null;
    case 'tool':
      return catalog.tools.find((tool) => tool.id === targetId)?.name ?? null;
    default:
      return null;
  }
}

/** Correlação do bloco expandido: entidades ligadas ao alvo do evento. */
export interface AuditCorrelation {
  attempt: Attempt | null;
  approval: Approval | null;
  task: Task | null;
}

export function correlateAuditEvent(event: AuditEvent, catalog: AuditCatalog): AuditCorrelation {
  const empty: AuditCorrelation = { attempt: null, approval: null, task: null };
  if (event.targetId === null) return empty;
  if (event.targetType === 'attempt') {
    const attempt = catalog.attempts.find((item) => item.id === event.targetId) ?? null;
    return {
      attempt,
      approval: null,
      task: catalog.tasks.find((task) => task.id === attempt?.taskId) ?? null,
    };
  }
  if (event.targetType === 'approval') {
    const approval = catalog.approvals.find((item) => item.id === event.targetId) ?? null;
    return {
      attempt: null,
      approval,
      task: catalog.tasks.find((task) => task.id === approval?.taskId) ?? null,
    };
  }
  if (event.targetType === 'task') {
    return {
      attempt: null,
      approval: null,
      task: catalog.tasks.find((task) => task.id === event.targetId) ?? null,
    };
  }
  return empty;
}

/**
 * Projeto correlacionável do evento (ver regra no topo do módulo).
 * `null` = não correlacionável → só aparece com filtro de projeto vazio.
 */
export function resolveEventProjectId(event: AuditEvent, catalog: AuditCatalog): Ulid | null {
  const { targetType, targetId } = event;
  if (targetId !== null) {
    const direct: Record<string, () => Ulid | null | undefined> = {
      task: () => catalog.tasks.find((task) => task.id === targetId)?.projectId,
      approval: () => catalog.approvals.find((approval) => approval.id === targetId)?.projectId,
      document: () => catalog.documents.find((document) => document.id === targetId)?.projectId,
      demand: () => catalog.demands.find((demand) => demand.id === targetId)?.projectId,
      solicitation: () =>
        catalog.solicitations.find((solicitation) => solicitation.id === targetId)?.projectId,
      workflow: () => catalog.workflows.find((workflow) => workflow.id === targetId)?.projectId,
      agent: () => catalog.agents.find((agent) => agent.id === targetId)?.projectId,
      attempt: () =>
        catalog.tasks.find(
          (task) =>
            task.id === catalog.attempts.find((attempt) => attempt.id === targetId)?.taskId,
        )?.projectId,
    };
    const resolved = direct[targetType]?.();
    if (resolved) return resolved;
  }
  // Fallback: ator é um agente alocado num projeto.
  return catalog.agents.find((agent) => agent.id === event.actorId)?.projectId ?? null;
}

/** Tarefa correlacionável do evento (alvo direto, attempt ou aprovação). */
function matchesTaskFilter(event: AuditEvent, taskId: Ulid, catalog: AuditCatalog): boolean {
  if (event.targetId === null) return false;
  if (event.targetType === 'task') return event.targetId === taskId;
  if (event.targetType === 'attempt') {
    return (
      catalog.attempts.find((attempt) => attempt.id === event.targetId)?.taskId === taskId
    );
  }
  if (event.targetType === 'approval') {
    return (
      catalog.approvals.find((approval) => approval.id === event.targetId)?.taskId === taskId
    );
  }
  return false;
}

/**
 * Casa o evento com TODOS os filtros preenchidos (AND). Filtros de entidade
 * só casam via `targetId` quando o `targetType` corresponde — um evento sem
 * alvo correlacionável (ex.: `targetId` nulo) fica de fora assim que o
 * filtro é preenchido (só aparece em "todos").
 */
export function matchesAuditFilters(
  event: AuditEvent,
  filters: AuditFilters,
  catalog: AuditCatalog,
): boolean {
  if (!matchesAuditSearch(event, filters.search)) return false;
  if (filters.actorKind !== '' && event.actorKind !== filters.actorKind) return false;
  if (filters.actorId !== '' && event.actorId !== filters.actorId) return false;
  if (filters.projectId !== '' && resolveEventProjectId(event, catalog) !== filters.projectId) {
    return false;
  }
  if (filters.taskId !== '' && !matchesTaskFilter(event, filters.taskId, catalog)) return false;
  if (filters.attemptId !== '') {
    if (event.targetType !== 'attempt' || event.targetId !== filters.attemptId) return false;
  }
  if (filters.modelId !== '') {
    if (event.targetType !== 'model' || event.targetId !== filters.modelId) return false;
  }
  if (filters.toolId !== '') {
    if (event.targetType !== 'tool' || event.targetId !== filters.toolId) return false;
  }
  // Período inclusivo nas duas pontas (comparação lexicográfica de ISO date).
  const day = event.occurredAt.slice(0, 10);
  if (filters.dateFrom !== '' && day < filters.dateFrom) return false;
  if (filters.dateTo !== '' && day > filters.dateTo) return false;
  return true;
}

/** Timeline final: filtrada e ordenada por `occurredAt` desc. */
export function filterAuditEvents(
  events: readonly AuditEvent[],
  filters: AuditFilters,
  catalog: AuditCatalog,
): AuditEvent[] {
  return sortAuditEventsDesc(events.filter((event) => matchesAuditFilters(event, filters, catalog)));
}
