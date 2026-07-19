import type { Attempt, Task } from '@/api';
import { downloadTextFile } from '@/features/governance/lib/audit-export';

/**
 * Exportação CSV do quadro (FR-3, item 7.3): conjunto FILTRADO visível na
 * tela, compatível com Excel — BOM UTF-8 + separador `;` (padrão do Excel
 * em locale pt-BR), aspas dobradas e célula cercada quando contém `;`,
 * aspas ou quebra de linha. Datas em ISO-8601; enums brutos (state,
 * priority, attempt state) por serem identificadores do contrato, não
 * texto de UI — mesma regra da exportação de auditoria.
 * XLSX NÃO é gerado: exigiria dependência nova injustificada (D-076).
 */

/** Cabeçalho fixo (identificadores, mesma ordem das colunas serializadas). */
export const BOARD_CSV_HEADER = [
  'id',
  'title',
  'state',
  'priority',
  'assignee',
  'createdAt',
  'updatedAt',
  'attempts',
  'lastAttemptState',
  'archived',
] as const;

/** Resumo das tentativas de uma tarefa (contagem + estado da mais recente). */
export interface TaskAttemptsSummary {
  count: number;
  lastState: Attempt['state'] | null;
}

/** Agrupa attempts por tarefa (contagem + estado da última por número). */
export function summarizeAttemptsByTask(
  attempts: readonly Attempt[],
): Map<string, TaskAttemptsSummary> {
  const byTask = new Map<string, Attempt[]>();
  for (const attempt of attempts) {
    const list = byTask.get(attempt.taskId) ?? [];
    list.push(attempt);
    byTask.set(attempt.taskId, list);
  }
  const summary = new Map<string, TaskAttemptsSummary>();
  for (const [taskId, list] of byTask) {
    const last = [...list].sort((a, b) => b.number - a.number)[0];
    summary.set(taskId, { count: list.length, lastState: last?.state ?? null });
  }
  return summary;
}

/** Escapa uma célula CSV (`;`): aspas dobradas + cerca se houver separador/quebra. */
function csvCell(value: string): string {
  if (/[";\r\n]/.test(value)) return `"${value.replace(/"/g, '""')}"`;
  return value;
}

/**
 * CSV das tarefas (já filtradas). `agentNames` resolve o responsável para
 * nome legível; tarefa sem responsável exporta célula vazia.
 */
export function boardTasksToCsv(
  tasks: readonly Task[],
  agentNames: ReadonlyMap<string, string>,
  attemptsByTask: ReadonlyMap<string, TaskAttemptsSummary>,
): string {
  const rows = tasks.map((task) => {
    const attempts = attemptsByTask.get(task.id);
    return [
      csvCell(task.id),
      csvCell(task.title),
      csvCell(task.state),
      csvCell(task.priority),
      csvCell(task.assigneeAgentId ? (agentNames.get(task.assigneeAgentId) ?? '') : ''),
      csvCell(task.createdAt),
      csvCell(task.updatedAt),
      String(attempts?.count ?? 0),
      csvCell(attempts?.lastState ?? ''),
      task.archivedAt === null ? 'no' : 'yes',
    ].join(';');
  });
  return [BOARD_CSV_HEADER.join(';'), ...rows].join('\n');
}

/** Nome do arquivo com a data da exportação: `poseidon-quadro-AAAA-MM-DD.csv`. */
export function boardCsvFilename(now: Date): string {
  return `poseidon-quadro-${now.toISOString().slice(0, 10)}.csv`;
}

/** Dispara o download do CSV com BOM UTF-8 (Excel reconhece acentos). */
export function downloadBoardCsv(filename: string, csv: string): void {
  downloadTextFile(filename, `\uFEFF${csv}`, 'text/csv;charset=utf-8');
}
