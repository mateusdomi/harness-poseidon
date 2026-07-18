import { useTranslation } from 'react-i18next';
import { TriangleAlert } from 'lucide-react';

import type { Task, TaskState } from '@/api';
import { Badge } from '@/design-system';
import { formatRelativeTime } from '@/lib/format';
import { priorityVariant, taskStateVariant } from '@/lib/status';
import { cn } from '@/lib/utils';

export interface TaskCardProps {
  task: Task;
  /** Nome do agente responsável (null = sem responsável). */
  agentName: string | null;
  /** Destaque discreto aplicado logo após movimento em tempo real. */
  justMoved: boolean;
  /** Relógio compartilhado do quadro (tempo relativo atualiza junto). */
  now: Date;
  onOpen: (taskId: string, state: TaskState) => void;
}

/**
 * Card compacto do Kanban: título, responsável, estado, prioridade,
 * indicador de bloqueio e tempo desde a última atividade. É um <button>
 * inteiro — focável e ativável por teclado, alvo de toque ≥ 44px.
 */
export function TaskCard({ task, agentName, justMoved, now, onOpen }: TaskCardProps) {
  const { t, i18n } = useTranslation();

  return (
    <li>
      <button
        type="button"
        onClick={() => onOpen(task.id, task.state)}
        aria-label={t('board.card.open', { title: task.title })}
        className={cn(
          'flex min-h-touch w-full flex-col gap-2 rounded-lg border border-border bg-surface p-3 text-left transition-colors hover:bg-surface-elevated focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent',
          // Movimento em tempo real: flash discreto (200 ms) — desligado com prefers-reduced-motion.
          justMoved && 'motion-safe:bg-surface-elevated motion-safe:ring-2 motion-safe:ring-info motion-safe:transition-shadow motion-safe:duration-200',
        )}
      >
        <span className="line-clamp-2 text-sm font-medium">{task.title}</span>
        <span className="flex flex-wrap items-center gap-1.5">
          <Badge variant={taskStateVariant(task.state)}>
            {t(`status.taskState.${task.state}`)}
          </Badge>
          <Badge variant={priorityVariant(task.priority)}>
            {t(`status.priority.${task.priority}`)}
          </Badge>
        </span>
        {task.state === 'blocked' && (
          <span className="flex items-start gap-1.5 text-xs text-error">
            <TriangleAlert aria-hidden="true" className="mt-0.5 size-3.5 shrink-0" />
            <span className="line-clamp-2">
              {t('board.card.blocked', {
                reason: task.blockedReason ?? t('board.card.blockedUnknown'),
              })}
            </span>
          </span>
        )}
        <span className="flex items-center justify-between gap-2 text-xs text-foreground-muted">
          <span className="truncate">{agentName ?? t('board.card.unassigned')}</span>
          <span className="shrink-0">
            {t('board.card.updated', {
              time: formatRelativeTime(task.updatedAt, i18n.language, now),
            })}
          </span>
        </span>
      </button>
    </li>
  );
}
