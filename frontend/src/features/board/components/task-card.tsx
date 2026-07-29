import { useTranslation } from 'react-i18next';
import { TriangleAlert } from 'lucide-react';

import type { Task, TaskState } from '@/api';
import { Badge } from '@/design-system';
import { shortTaskId } from '@/features/board/lib/board-derive';
import { isTaskStuck } from '@/features/board/lib/board-filters';
import {
  resolveBoardPresentation,
  taskStateLabelKey,
  unassignedLabelKey,
} from '@/features/board/lib/board-presentation';
import { formatRelativeTime } from '@/lib/format';
import { priorityVariant, taskStateVariant } from '@/lib/status';
import { cn } from '@/lib/utils';

export interface TaskCardProps {
  task: Task;
  /** Nome do agente responsável (null = sem responsável). */
  agentName: string | null;
  showTechnicalDetails: boolean;
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
export function TaskCard({
  task,
  agentName,
  showTechnicalDetails,
  justMoved,
  now,
  onOpen,
}: TaskCardProps) {
  const { t, i18n } = useTranslation();
  const stuck = isTaskStuck(task, now);
  const presentation = resolveBoardPresentation(showTechnicalDetails);
  const publicAgentName = agentName?.split(/\s+[—–]\s+/u)[0]?.trim() || null;

  return (
    <li>
      <button
        type="button"
        onClick={() => onOpen(task.id, task.state)}
        aria-label={t('board.card.open', { title: task.title })}
        className={cn(
          'flex min-h-touch w-full flex-col gap-2 rounded-lg border border-border bg-surface p-3 text-left transition-colors hover:bg-surface-elevated focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent',
          // Arquivada (metaestado): indicação visual discreta.
          task.archivedAt !== null && 'opacity-75',
          // Movimento em tempo real: flash discreto (200 ms) — desligado com prefers-reduced-motion.
          justMoved &&
            'motion-safe:bg-surface-elevated motion-safe:ring-2 motion-safe:ring-info motion-safe:transition-shadow motion-safe:duration-200',
        )}
      >
        <span className="flex min-w-0 items-start justify-between gap-2">
          <span className="line-clamp-2 min-w-0 text-sm font-medium">{task.title}</span>
          {presentation.showInternalId && (
            <span
              className="shrink-0 font-mono text-[0.7rem] text-foreground-muted"
              title={t('board.card.idLabel', { id: task.id })}
            >
              #{shortTaskId(task.id)}
            </span>
          )}
        </span>
        <span className="flex flex-wrap items-center gap-1.5">
          <Badge variant={taskStateVariant(task.state)}>
            {t(taskStateLabelKey(task.state, showTechnicalDetails))}
          </Badge>
          <Badge variant={priorityVariant(task.priority)}>
            {showTechnicalDetails
              ? t(`status.priority.${task.priority}`)
              : t('board.card.priority', {
                  priority: t(`status.priority.${task.priority}`).toLocaleLowerCase(),
                })}
          </Badge>
          {task.archivedAt !== null && <Badge variant="info">{t('board.card.archived')}</Badge>}
          {presentation.showCardType && (
            <Badge
              variant={
                task.cardType === 'human_gate'
                  ? 'warning'
                  : task.cardType === 'decision'
                    ? 'brand'
                    : 'info'
              }
            >
              {t(`board.card.types.${task.cardType ?? 'agent_task'}`)}
            </Badge>
          )}
          {task.phaseName && <Badge variant="brand">{task.phaseName}</Badge>}
        </span>
        {task.state === 'blocked' && (
          <span className="flex items-start gap-1.5 text-xs text-error">
            <TriangleAlert aria-hidden="true" className="mt-0.5 size-3.5 shrink-0" />
            <span className="line-clamp-2">
              {t(showTechnicalDetails ? 'board.card.blocked' : 'board.card.blockedBusiness', {
                reason: task.blockedReason ?? t('board.card.blockedUnknown'),
              })}
            </span>
          </span>
        )}
        {stuck && (
          <span className="flex items-start gap-1.5 text-xs text-warning">
            <TriangleAlert aria-hidden="true" className="mt-0.5 size-3.5 shrink-0" />
            <span>{t('board.card.stuck')}</span>
          </span>
        )}
        <span className="flex items-center justify-between gap-2 text-xs text-foreground-muted">
          <span className="truncate">
            {publicAgentName ?? t(unassignedLabelKey(showTechnicalDetails))}
          </span>
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
