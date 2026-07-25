import { useEffect, useMemo, useRef } from 'react';
import { useTranslation } from 'react-i18next';

import { type Agent, type Task, type TaskState } from '@/api';
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
 * Ordem principal canônica. Bloqueada é transversal e fica ao final, fora
 * da progressão linear.
 */
const KANBAN_STATES: readonly TaskState[] = [
  'backlog',
  'ready',
  'development',
  'review',
  'corrections',
  'testsGates',
  'done',
  'blocked',
];

/**
 * Quadro Kanban horizontal em todos os breakpoints, com colunas de largura
 * consistente. O filtro `?state=` destaca e rola até a coluna.
 */
export function KanbanBoard({
  tasks,
  agents,
  filteredState,
  recentlyMoved,
  now,
  onOpenTask,
}: KanbanBoardProps) {
  const { t } = useTranslation();
  const tasksByState = useMemo(() => groupTasksByState(tasks), [tasks]);
  const agentNames = useMemo(
    () => new Map(agents.map((agent) => [agent.id, agent.name])),
    [agents],
  );
  const columnRefs = useRef(new Map<TaskState, HTMLElement>());
  const pan = useRef<{ pointerId: number; startX: number; scrollLeft: number; moved: boolean } | null>(
    null,
  );
  const scrollerRef = useRef<HTMLDivElement>(null);

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
    <div
      ref={scrollerRef}
      className="flex cursor-grab snap-x snap-mandatory gap-3 overflow-x-auto pb-3 data-[panning=true]:cursor-grabbing data-[panning=true]:select-none"
      role="region"
      aria-label={t('board.kanbanLabel')}
      tabIndex={0}
      onPointerDown={(event) => {
        if (
          event.button !== 0 ||
          !(event.target instanceof Element) ||
          event.target.closest('button, a, input, select, textarea, [data-no-board-pan]')
        ) {
          return;
        }
        const scroller = scrollerRef.current;
        if (!scroller) return;
        pan.current = {
          pointerId: event.pointerId,
          startX: event.clientX,
          scrollLeft: scroller.scrollLeft,
          moved: false,
        };
        scroller.setPointerCapture(event.pointerId);
        scroller.dataset.panning = 'true';
      }}
      onPointerMove={(event) => {
        const state = pan.current;
        const scroller = scrollerRef.current;
        if (!state || !scroller || state.pointerId !== event.pointerId) return;
        const delta = event.clientX - state.startX;
        if (Math.abs(delta) > 4) state.moved = true;
        if (!state.moved) return;
        event.preventDefault();
        scroller.scrollLeft = state.scrollLeft - delta;
      }}
      onPointerUp={(event) => {
        const scroller = scrollerRef.current;
        if (pan.current?.pointerId !== event.pointerId || !scroller) return;
        if (scroller.hasPointerCapture(event.pointerId)) {
          scroller.releasePointerCapture(event.pointerId);
        }
        delete scroller.dataset.panning;
        pan.current = null;
      }}
      onPointerCancel={() => {
        if (scrollerRef.current) delete scrollerRef.current.dataset.panning;
        pan.current = null;
      }}
    >
      {KANBAN_STATES.map((state) => (
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
