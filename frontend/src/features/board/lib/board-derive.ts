import { TASK_STATES, taskStateSchema, type Agent, type Task, type TaskState } from '@/api';

/**
 * Agrupa tarefas pelas 8 colunas do quadro (todas sempre presentes,
 * mesmo vazias), ordenadas pela última atividade (mais recente primeiro).
 */
export function groupTasksByState(tasks: Task[]): Record<TaskState, Task[]> {
  const grouped: Record<TaskState, Task[]> = {
    backlog: [],
    ready: [],
    development: [],
    review: [],
    corrections: [],
    testsGates: [],
    blocked: [],
    done: [],
  };
  for (const task of tasks) grouped[task.state].push(task);
  for (const state of TASK_STATES) {
    grouped[state].sort((a, b) => b.updatedAt.localeCompare(a.updatedAt));
  }
  return grouped;
}

/** Valida o query param `?state=` contra o enum de colunas. */
export function parseTaskStateParam(value: string | null): TaskState | null {
  if (value === null) return null;
  const parsed = taskStateSchema.safeParse(value);
  return parsed.success ? parsed.data : null;
}

/**
 * Sufixo curto e legível do ULID (últimos 6 chars) para exibir no card.
 * ULIDs já são Crockford base32 em caixa alta; o `toUpperCase` é defensivo.
 * O sufixo é um pedaço do ID completo, então a busca por ID (substring)
 * casa com o que o usuário vê no card.
 */
export function shortTaskId(id: string): string {
  return id.slice(-6).toUpperCase();
}

/**
 * Agentes que são responsáveis REAIS por ao menos uma tarefa do conjunto
 * — a lista de opções do filtro "Responsável". Derivar do dado real
 * (`assigneeAgentId`) elimina opções mortas (ex.: chefes, que orquestram
 * mas não recebem cards) e garante que todo item filtra de verdade.
 * Ordena por nome legível (o mesmo `agent.name` mostrado no card).
 */
export function assigneeAgents(tasks: Task[], agents: Agent[]): Agent[] {
  const assigned = new Set<string>();
  for (const task of tasks) {
    if (task.assigneeAgentId !== null) assigned.add(task.assigneeAgentId);
  }
  return agents
    .filter((agent) => assigned.has(agent.id))
    .sort((a, b) => a.name.localeCompare(b.name));
}
