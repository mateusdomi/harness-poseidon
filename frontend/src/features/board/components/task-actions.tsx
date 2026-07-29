import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { Info } from 'lucide-react';

import { PRIORITIES, type Priority, type Task } from '@/api';
import { Button, Field, Select } from '@/design-system';
import {
  useArchiveTask,
  useMoveTask,
  useSetTaskPriority,
  useUnarchiveTask,
} from '@/features/board/hooks/use-board';

export interface TaskActionsProps {
  task: Task;
  showTechnicalDetails?: boolean;
}

/**
 * Ações humanas permitidas sobre a tarefa: repriorizar, pausar, cancelar,
 * solicitar revisão e arquivar/desarquivar (metaestado — arquivar só é
 * permitido para concluídas; desarquivar sempre). O humano NÃO cria nem
 * edita tarefa técnica — o texto explicativo reforça a cadeia
 * solicitação → demanda → tarefa e aponta o chat como porta de entrada.
 *
 * Mapeamento com o contrato atual (ver DECISIONS):
 * - pausar → moveTask('blocked') com nota de pausa (vira bloqueio manual);
 * - cancelar → moveTask('backlog') com nota (sai do fluxo; chefe replaneja);
 * - solicitar revisão → moveTask('review') com nota;
 * - arquivar/desarquivar → archiveTask/unarchiveTask (não muda `state`).
 */
export function TaskActions({
  task,
  showTechnicalDetails = false,
}: TaskActionsProps) {
  const { t } = useTranslation();
  const moveTask = useMoveTask();
  const setPriority = useSetTaskPriority();
  const archiveTask = useArchiveTask();
  const unarchiveTask = useUnarchiveTask();
  const busy =
    moveTask.isPending || setPriority.isPending || archiveTask.isPending ||
    unarchiveTask.isPending;
  const failed =
    moveTask.isError || setPriority.isError || archiveTask.isError || unarchiveTask.isError;
  const archived = task.archivedAt !== null;

  return (
    <div className="flex flex-col gap-3">
      <p className="flex items-start gap-2 rounded-lg border border-border bg-surface p-3 text-xs text-foreground-muted">
        <Info aria-hidden="true" className="mt-0.5 size-4 shrink-0" />
        <span>
          {t(
            showTechnicalDetails
              ? 'board.detail.actions.explain'
              : 'board.detail.actions.explainBusiness',
          )}{' '}
          <Link
            to="/chat"
            className="font-medium text-brand-strong underline-offset-4 hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
          >
            {t('board.detail.actions.chatCta')}
          </Link>
        </span>
      </p>

      <Field htmlFor="task-priority" label={t('board.detail.actions.priorityLabel')}>
        <Select
          id="task-priority"
          value={task.priority}
          disabled={busy}
          onChange={(event) =>
            setPriority.mutate({
              taskId: task.id,
              input: { priority: event.target.value as Priority },
            })
          }
        >
          {PRIORITIES.map((priority) => (
            <option key={priority} value={priority}>
              {t(`status.priority.${priority}`)}
            </option>
          ))}
        </Select>
      </Field>

      <div className="flex flex-wrap gap-2">
        <Button
          type="button"
          variant="outline"
          size="sm"
          disabled={busy || task.state === 'blocked' || task.state === 'done'}
          onClick={() =>
            moveTask.mutate({
              taskId: task.id,
              input: { toState: 'blocked', note: t('board.detail.actions.pauseNote') },
            })
          }
        >
          {t('board.detail.actions.pause')}
        </Button>
        <Button
          type="button"
          variant="outline"
          size="sm"
          disabled={busy || task.state === 'review' || task.state === 'done'}
          onClick={() =>
            moveTask.mutate({
              taskId: task.id,
              input: { toState: 'review', note: t('board.detail.actions.reviewNote') },
            })
          }
        >
          {t('board.detail.actions.requestReview')}
        </Button>
        <Button
          type="button"
          variant="destructive"
          size="sm"
          disabled={busy || task.state === 'backlog' || task.state === 'done'}
          onClick={() =>
            moveTask.mutate({
              taskId: task.id,
              input: { toState: 'backlog', note: t('board.detail.actions.cancelNote') },
            })
          }
        >
          {t('board.detail.actions.cancel')}
        </Button>
        {archived ? (
          <Button
            type="button"
            variant="outline"
            size="sm"
            disabled={busy}
            onClick={() => unarchiveTask.mutate(task.id)}
          >
            {t('board.detail.actions.unarchive')}
          </Button>
        ) : (
          <Button
            type="button"
            variant="outline"
            size="sm"
            disabled={busy || task.state !== 'done'}
            title={task.state === 'done' ? undefined : t('board.detail.actions.archiveOnlyDone')}
            onClick={() => archiveTask.mutate(task.id)}
          >
            {t('board.detail.actions.archive')}
          </Button>
        )}
      </div>

      {failed && (
        <p role="alert" className="text-sm text-error">
          {t('board.detail.actions.error')}
        </p>
      )}
    </div>
  );
}
