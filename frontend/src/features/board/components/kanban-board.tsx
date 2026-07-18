import { useEffect, useMemo, useRef } from 'react';

import { TASK_STATES, type Agent, type Task, type TaskState } from '@/api';
import { BoardColumn } from '@/features/board/components/board-column';
import { groupTasksByState } from '@/features/board/lib/board-derive';

export interface KanbanBoardProps {
  tasks: Task[];
  agents: Agent[];
  /** Filtro `?state=` (do cockpit): destaca e rola até a coluna. */
  filteredState: TaskState | null;
  recentlyMoved: ReadonlySet<string>;
  now: Date;
  onOpenTask: (taskId: string, state: TaskState) => void;
}

/**
 * Quadro Kanban com as 8 colunas do domínio: scroll horizontal no mobile
 * (colunas de largura fixa) e grid no desktop. O filtro `?state=` destaca
 * a coluna e rola até ela (respeitando prefers-reduced-motion).
 */
export function KanbanBoard({
  tasks,
  agents,
  filteredState,
  recentlyMoved,
  now,
  onOpenTask,
}: KanbanBoardProps) {
  const tasksByState = useMemo(() => groupTasksByState(tasks), [tasks]);
  const agentNames = useMemo(
    () => new Map(agents.map((agent) => [agent.id, agent.name])),
    [agents],
  );
  const columnRefs = useRef(new Map<TaskState, HTMLElement>());

  useEffect(() => {
    if (!filteredState) return;
    const column = columnRefs.current.get(filteredState);
    if (!column) return;
    const reducedMotion =
      typeof window.matchMedia === 'function' &&
      window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    column.scrollIntoView?.({
      behavior: reducedMotion ? 'auto' : 'smooth',
      block: 'nearest',
      inline: 'center',
    });
  }, [filteredState]);

  return (
    <div className="flex gap-3 overflow-x-auto pb-2 lg:grid lg:grid-cols-2 lg:overflow-visible xl:grid-cols-4">
      {TASK_STATES.map((state) => (
        <BoardColumn
          key={state}
          ref={(element) => {
            if (element) columnRefs.current.set(state, element);
            else columnRefs.current.delete(state);
          }}
          state={state}
          tasks={tasksByState[state]}
          agentNames={agentNames}
          highlighted={filteredState === state}
          recentlyMoved={recentlyMoved}
          now={now}
          onOpenTask={onOpenTask}
        />
      ))}
    </div>
  );
}
