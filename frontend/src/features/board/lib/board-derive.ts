import { TASK_STATES, taskStateSchema, type Task, type TaskState } from '@/api';

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
