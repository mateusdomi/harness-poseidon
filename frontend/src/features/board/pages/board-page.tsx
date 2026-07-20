import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useSearchParams } from 'react-router-dom';
import { ArrowLeft, Info } from 'lucide-react';

import { Button, Card, CardContent, Select, Skeleton } from '@/design-system';
import { useApi } from '@/app/api-context';
import { BoardFiltersBar } from '@/features/board/components/board-filters-bar';
import { BoardFlowDialog } from '@/features/board/components/board-flow-dialog';
import { KanbanBoard } from '@/features/board/components/kanban-board';
import { TaskDetail } from '@/features/board/components/task-detail';
import { TaskDrawer } from '@/features/board/components/task-drawer';
import { ModalDialog } from '@/features/shared/components/modal-dialog';
import {
  useArchiveCompletedTasks,
  useBoardAgents,
  useBoardRealtime,
  useBoardTasks,
  useNow,
} from '@/features/board/hooks/use-board';
import { useMediaQuery } from '@/features/board/hooks/use-media-query';
import {
  boardFiltersToSearchParams,
  filterBoardTasks,
  parseBoardFilters,
  type BoardFilters,
} from '@/features/board/lib/board-filters';
import {
  boardCsvFilename,
  boardTasksToCsv,
  downloadBoardCsv,
  summarizeAttemptsByTask,
} from '@/features/board/lib/board-export';
import { useActiveProject } from '@/features/shared/hooks/use-active-project';

/**
 * Quadro Kanban do projeto ativo. Query params:
 * - `?state=<coluna>` (vindo do cockpit): filtra a coluna, destaca e rola
 *   até ela (o filtro de coluna da barra usa o MESMO param — D-074);
 * - `?task=<id>`: abre o detalhe — drawer no desktop (lg+), página
 *   dedicada no mobile. Deep-linkável nos dois modos;
 * - `?q=`, `?agent=`, `?priority=`, `?period=`, `?archive=`: filtros da
 *   barra (padrões omitidos; `?task=` preservado em todas as operações).
 */
export default function UboardPage() {
  const { t } = useTranslation();
  const api = useApi();
  const [searchParams, setSearchParams] = useSearchParams();
  const isDesktop = useMediaQuery('(min-width: 1024px)');

  const { projects, activeProject, setActiveProject, isPending, isError, refetch } =
    useActiveProject();
  const projectId = activeProject?.id ?? null;

  const tasksQuery = useBoardTasks(projectId);
  const agentsQuery = useBoardAgents(projectId);
  const recentlyMoved = useBoardRealtime(projectId);
  const archiveCompleted = useArchiveCompletedTasks();
  const now = useNow();

  const [flowOpen, setFlowOpen] = useState(false);
  const [archiveAllOpen, setArchiveAllOpen] = useState(false);
  const [exportPending, setExportPending] = useState(false);

  const filters = useMemo(() => parseBoardFilters(searchParams), [searchParams]);
  const openTaskId = searchParams.get('task');

  function patchSearchParams(updater: (previous: URLSearchParams) => URLSearchParams) {
    setSearchParams(updater, { preventScrollReset: true });
  }

  function openTask(taskId: string) {
    patchSearchParams((previous) => {
      const next = new URLSearchParams(previous);
      next.set('task', taskId);
      return next;
    });
  }

  function closeTask() {
    patchSearchParams((previous) => {
      const next = new URLSearchParams(previous);
      next.delete('task');
      return next;
    });
  }

  function changeFilters(next: BoardFilters) {
    patchSearchParams((previous) => boardFiltersToSearchParams(previous, next));
  }

  function clearFilters() {
    patchSearchParams((previous) => {
      const next = new URLSearchParams();
      const task = previous.get('task');
      if (task) next.set('task', task);
      return next;
    });
  }

  const loading = isPending || tasksQuery.isPending || agentsQuery.isPending;
  const errored = isError || tasksQuery.isError || agentsQuery.isError;
  const tasks = useMemo(() => tasksQuery.data ?? [], [tasksQuery.data]);
  const agents = agentsQuery.data ?? [];

  const filteredTasks = useMemo(
    () => filterBoardTasks(tasks, filters, now),
    [tasks, filters, now],
  );
  const filteredState = filters.state === '' ? null : filters.state;
  // Elegíveis ao arquivamento em lote: concluídas e ainda ativas (do projeto).
  const archivableTasks = useMemo(
    () => tasks.filter((task) => task.state === 'done' && task.archivedAt === null),
    [tasks],
  );

  async function exportCsv() {
    setExportPending(true);
    try {
      const attempts = (await api.list('attempts')).items;
      const agentNames = new Map(agents.map((agent) => [agent.id, agent.name]));
      const csv = boardTasksToCsv(filteredTasks, agentNames, summarizeAttemptsByTask(attempts));
      downloadBoardCsv(boardCsvFilename(new Date()), csv);
    } finally {
      setExportPending(false);
    }
  }

  function retryAll() {
    refetch();
    void tasksQuery.refetch();
    void agentsQuery.refetch();
  }

  // Detalhe em página dedicada (mobile): substitui o quadro.
  if (openTaskId && !isDesktop && !loading && !errored) {
    return (
      <div className="flex flex-col gap-4">
        <Button type="button" variant="ghost" size="sm" className="self-start" onClick={closeTask}>
          <ArrowLeft aria-hidden="true" />
          {t('board.detail.back')}
        </Button>
        <TaskDetail taskId={openTaskId} agents={agents} />
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="font-heading text-2xl font-semibold">{t('features.board.title')}</h1>
        {projects.length > 0 && (
          <div className="ml-auto flex items-center gap-2">
            <label htmlFor="board-project" className="text-sm text-foreground-muted">
              {t('cockpit.projectSelector.label')}
            </label>
            <Select
              id="board-project"
              className="w-auto min-w-48"
              value={activeProject?.id ?? ''}
              onChange={(event) => setActiveProject(event.target.value)}
            >
              {projects.map((project) => (
                <option key={project.id} value={project.id}>
                  {project.name}
                </option>
              ))}
            </Select>
          </div>
        )}
      </div>

      {loading ? (
        <div
          className="flex gap-3 overflow-x-auto lg:grid lg:grid-cols-2 xl:grid-cols-4"
          role="status"
          aria-label={t('common.states.loading')}
        >
          {Array.from({ length: 8 }, (_, index) => (
            <Skeleton key={index} className="h-64 w-72 shrink-0 lg:w-auto" />
          ))}
        </div>
      ) : errored ? (
        <div className="flex flex-col items-start gap-3">
          <p role="alert" className="text-sm text-error">
            {t('common.states.errorBody')}
          </p>
          <Button type="button" variant="outline" onClick={retryAll}>
            {t('common.actions.retry')}
          </Button>
        </div>
      ) : !activeProject ? (
        <Card>
          <CardContent className="flex flex-col items-start gap-3 p-6">
            <p className="text-sm text-foreground-muted">{t('board.noProject.body')}</p>
            <Button asChild>
              <Link to="/projects">{t('board.noProject.cta')}</Link>
            </Button>
          </CardContent>
        </Card>
      ) : (
        <>
          <p className="flex items-start gap-2 rounded-lg border border-border bg-surface p-3 text-xs text-foreground-muted">
            <Info aria-hidden="true" className="mt-0.5 size-4 shrink-0" />
            <span>
              {t('board.hint')}{' '}
              <Link
                to="/chat"
                className="font-medium text-brand-strong underline-offset-4 hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
              >
                {t('board.hintCta')}
              </Link>
            </span>
          </p>

          {tasks.length === 0 ? (
            <Card>
              <CardContent className="flex flex-col items-start gap-3 p-6">
                <h2 className="font-heading text-lg font-semibold">{t('board.empty.title')}</h2>
                <p className="text-sm text-foreground-muted">{t('board.empty.body')}</p>
                <Button asChild>
                  <Link to="/chat">{t('board.empty.cta')}</Link>
                </Button>
              </CardContent>
            </Card>
          ) : (
            <>
              <BoardFiltersBar
                filters={filters}
                agents={agents}
                filteredCount={filteredTasks.length}
                totalCount={tasks.length}
                archivableCount={archivableTasks.length}
                exportPending={exportPending}
                onFiltersChange={changeFilters}
                onClear={clearFilters}
                onExport={() => void exportCsv()}
                onArchiveCompleted={() => setArchiveAllOpen(true)}
                onShowFlow={() => setFlowOpen(true)}
              />
              <KanbanBoard
                tasks={filteredTasks}
                agents={agents}
                filteredState={filteredState}
                recentlyMoved={recentlyMoved}
                now={now}
                onOpenTask={openTask}
              />
            </>
          )}
        </>
      )}

      {openTaskId && isDesktop && (
        <TaskDrawer taskId={openTaskId} agents={agents} onClose={closeTask} />
      )}

      {flowOpen && <BoardFlowDialog onClose={() => setFlowOpen(false)} />}

      {archiveAllOpen && (
        <ModalDialog
          label={t('board.archive.batchTitle')}
          onClose={() => setArchiveAllOpen(false)}
        >
          <h2 className="font-heading text-lg font-semibold">{t('board.archive.batchTitle')}</h2>
          <p className="text-sm text-foreground-muted">
            {t('board.archive.batchBody', { count: archivableTasks.length })}
          </p>
          <div className="flex flex-wrap gap-2">
            <Button
              type="button"
              size="sm"
              disabled={archiveCompleted.isPending}
              onClick={() =>
                void archiveCompleted.mutateAsync(archivableTasks).then(() => {
                  setArchiveAllOpen(false);
                })
              }
            >
              {t('board.archive.batchConfirm', { count: archivableTasks.length })}
            </Button>
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={archiveCompleted.isPending}
              onClick={() => setArchiveAllOpen(false)}
            >
              {t('common.actions.cancel')}
            </Button>
          </div>
          {archiveCompleted.isError && (
            <p role="alert" className="text-sm text-error">
              {t('board.archive.error')}
            </p>
          )}
        </ModalDialog>
      )}
    </div>
  );
}
