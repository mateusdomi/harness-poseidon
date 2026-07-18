import { useTranslation } from 'react-i18next';
import { Link, useSearchParams } from 'react-router-dom';
import { ArrowLeft, Info } from 'lucide-react';

import { Button, Card, CardContent, Select, Skeleton } from '@/design-system';
import { KanbanBoard } from '@/features/board/components/kanban-board';
import { TaskDetail } from '@/features/board/components/task-detail';
import { TaskDrawer } from '@/features/board/components/task-drawer';
import { useBoardAgents, useBoardRealtime, useBoardTasks, useNow } from '@/features/board/hooks/use-board';
import { useMediaQuery } from '@/features/board/hooks/use-media-query';
import { parseTaskStateParam } from '@/features/board/lib/board-derive';
import { useActiveProject } from '@/features/shared/hooks/use-active-project';

/**
 * Quadro Kanban do projeto ativo. Query params:
 * - `?state=<coluna>` (vindo do cockpit): destaca e rola até a coluna;
 * - `?task=<id>`: abre o detalhe — drawer no desktop (lg+), página
 *   dedicada no mobile. Deep-linkável nos dois modos.
 */
export default function UboardPage() {
  const { t } = useTranslation();
  const [searchParams, setSearchParams] = useSearchParams();
  const isDesktop = useMediaQuery('(min-width: 1024px)');

  const { projects, activeProject, setActiveProject, isPending, isError, refetch } =
    useActiveProject();
  const projectId = activeProject?.id ?? null;

  const tasksQuery = useBoardTasks(projectId);
  const agentsQuery = useBoardAgents(projectId);
  const recentlyMoved = useBoardRealtime(projectId);
  const now = useNow();

  const filteredState = parseTaskStateParam(searchParams.get('state'));
  const openTaskId = searchParams.get('task');

  function openTask(taskId: string) {
    setSearchParams(
      (previous) => {
        const next = new URLSearchParams(previous);
        next.set('task', taskId);
        return next;
      },
      { preventScrollReset: true },
    );
  }

  function closeTask() {
    setSearchParams(
      (previous) => {
        const next = new URLSearchParams(previous);
        next.delete('task');
        return next;
      },
      { preventScrollReset: true },
    );
  }

  const loading = isPending || tasksQuery.isPending || agentsQuery.isPending;
  const errored = isError || tasksQuery.isError || agentsQuery.isError;
  const tasks = tasksQuery.data ?? [];
  const agents = agentsQuery.data ?? [];

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
                className="font-medium text-brand underline-offset-4 hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
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
            <KanbanBoard
              tasks={tasks}
              agents={agents}
              filteredState={filteredState}
              recentlyMoved={recentlyMoved}
              now={now}
              onOpenTask={openTask}
            />
          )}
        </>
      )}

      {openTaskId && isDesktop && (
        <TaskDrawer taskId={openTaskId} agents={agents} onClose={closeTask} />
      )}
    </div>
  );
}
