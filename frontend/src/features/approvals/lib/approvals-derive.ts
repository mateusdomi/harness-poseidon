import type { Approval, Priority } from '@/api';

/**
 * Derivações da fila consolidada de aprovações: tipo do item (pelo
 * vínculo), ordenação sensata (prazo → criticidade → mais antigo) e
 * filtros de prazo. Funções puras, testadas sem render.
 */

/** Tipo da decisão, derivado do vínculo preenchido. */
export type ApprovalKind = 'gate' | 'document' | 'task' | 'decision';

export function approvalKind(approval: Approval): ApprovalKind {
  if (approval.gateId !== null) return 'gate';
  if (approval.documentId !== null) return 'document';
  if (approval.taskId !== null) return 'task';
  return 'decision';
}

/** Severidade para ordenação: critical primeiro. */
const PRIORITY_ORDER: Record<Priority, number> = {
  critical: 0,
  high: 1,
  medium: 2,
  low: 3,
};

/** Filtro de prazo da fila. */
export type DueFilter = 'all' | 'overdue' | 'week' | 'none';

const WEEK_MS = 7 * 24 * 60 * 60 * 1000;

export function matchesDueFilter(approval: Approval, filter: DueFilter, now: Date): boolean {
  switch (filter) {
    case 'all':
      return true;
    case 'overdue':
      return approval.dueAt !== null && new Date(approval.dueAt).getTime() < now.getTime();
    case 'week':
      return (
        approval.dueAt !== null &&
        new Date(approval.dueAt).getTime() <= now.getTime() + WEEK_MS
      );
    case 'none':
      return approval.dueAt === null;
  }
}

/**
 * Ordenação da fila: prazo mais próximo primeiro (sem prazo por último),
 * desempate por criticidade (critical primeiro) e depois mais antigo.
 */
export function sortQueue(approvals: Approval[]): Approval[] {
  return [...approvals].sort((a, b) => {
    if (a.dueAt !== null && b.dueAt !== null) {
      const byDue = a.dueAt.localeCompare(b.dueAt);
      if (byDue !== 0) return byDue;
    } else if (a.dueAt !== null) {
      return -1;
    } else if (b.dueAt !== null) {
      return 1;
    }
    const byPriority = PRIORITY_ORDER[a.priority] - PRIORITY_ORDER[b.priority];
    if (byPriority !== 0) return byPriority;
    return a.requestedAt.localeCompare(b.requestedAt);
  });
}
