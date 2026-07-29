import { forwardRef } from 'react';
import { useTranslation } from 'react-i18next';

import type { Task, TaskState } from '@/api';
import { Badge } from '@/design-system';
import { cn } from '@/lib/utils';
import { TaskCard } from '@/features/board/components/task-card';
import { taskStateLabelKey } from '@/features/board/lib/board-presentation';

export interface BoardColumnProps {
  state: TaskState;
  tasks: Task[];
  /** Mapa agente → nome para o responsável do card. */
  agentNames: Map<string, string>;
  showTechnicalDetails: boolean;
  /** Coluna destacada pelo filtro `?state=` (vinda do cockpit). */
  highlighted: boolean;
  recentlyMoved: ReadonlySet<string>;
  now: Date;
  onOpenTask: (taskId: string, state: TaskState) => void;
}

/**
 * Coluna do Kanban: região com heading (a11y), contador e lista de cards.
 * Largura fixa em todos os breakpoints para preservar o fluxo horizontal.
 */
export const BoardColumn = forwardRef<HTMLElement, BoardColumnProps>(function BoardColumn(
  { state, tasks, agentNames, showTechnicalDetails, highlighted, recentlyMoved, now, onOpenTask },
  ref,
) {
  const { t } = useTranslation();

  return (
    <section
      ref={ref}
      aria-labelledby={`board-column-${state}`}
      data-state={state}
      data-highlighted={highlighted || undefined}
      className={cn(
        'flex w-72 shrink-0 snap-start flex-col gap-2 rounded-lg bg-surface-elevated p-2 sm:w-80',
        highlighted && 'bg-surface ring-2 ring-accent',
      )}
    >
      <h3
        id={`board-column-${state}`}
        className="sticky top-0 z-10 flex items-center gap-2 rounded-md bg-surface-elevated/95 px-1 py-2 font-heading text-sm font-semibold backdrop-blur"
      >
        {t(taskStateLabelKey(state, showTechnicalDetails))}
        <Badge variant="outline">{tasks.length}</Badge>
      </h3>
      {tasks.length === 0 ? (
        <p className="px-1 pb-2 text-xs text-foreground-muted">{t('board.column.empty')}</p>
      ) : (
        <ul className="flex flex-col gap-2">
          {tasks.map((task) => (
            <TaskCard
              key={task.id}
              task={task}
              agentName={
                task.assigneeAgentId ? (agentNames.get(task.assigneeAgentId) ?? null) : null
              }
              showTechnicalDetails={showTechnicalDetails}
              justMoved={recentlyMoved.has(task.id)}
              now={now}
              onOpen={onOpenTask}
            />
          ))}
        </ul>
      )}
    </section>
  );
});
