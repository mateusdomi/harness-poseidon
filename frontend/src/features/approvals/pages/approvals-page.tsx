import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { CheckCircle2 } from 'lucide-react';

import { prioritySchema, type Priority } from '@/api';
import { Button, Card, CardContent, Select, Skeleton } from '@/design-system';
import { ApprovalItem } from '@/features/approvals/components/approval-item';
import {
  useApprovalsQueue,
  useApprovalsRealtime,
} from '@/features/approvals/hooks/use-approvals';
import {
  matchesDueFilter,
  sortQueue,
  type DueFilter,
} from '@/features/approvals/lib/approvals-derive';
import { useNow } from '@/features/board/hooks/use-board';

/**
 * Fila consolidada de aprovações e decisões: gates, documentos, mudanças
 * de modo e decisões humanas pendentes, com filtros por projeto,
 * criticidade e prazo. Ordenação: prazo → criticidade → mais antigo.
 */
export default function UapprovalsPage() {
  const { t } = useTranslation();
  const { approvals, projects, gates, documents, tasks, isPending, isError, refetch } =
    useApprovalsQueue();
  useApprovalsRealtime(projects.map((project) => project.id));
  const now = useNow();

  const [projectFilter, setProjectFilter] = useState('');
  const [priorityFilter, setPriorityFilter] = useState<Priority | ''>('');
  const [dueFilter, setDueFilter] = useState<DueFilter>('all');

  const queue = useMemo(() => {
    let result = approvals.filter((approval) => approval.state === 'pending');
    if (projectFilter !== '') {
      result = result.filter((approval) => approval.projectId === projectFilter);
    }
    if (priorityFilter !== '') {
      result = result.filter((approval) => approval.priority === priorityFilter);
    }
    result = result.filter((approval) => matchesDueFilter(approval, dueFilter, now));
    return sortQueue(result);
  }, [approvals, projectFilter, priorityFilter, dueFilter, now]);

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="font-heading text-2xl font-semibold">{t('features.approvals.title')}</h1>
        {!isPending && !isError && (
          <span className="text-sm text-foreground-muted">
            {t('approvals.queue.count', { count: queue.length })}
          </span>
        )}
      </div>

      {isPending ? (
        <div className="flex flex-col gap-3" role="status" aria-label={t('common.states.loading')}>
          <Skeleton className="h-10 w-full" />
          {Array.from({ length: 3 }, (_, index) => (
            <Skeleton key={index} className="h-32 w-full" />
          ))}
        </div>
      ) : isError ? (
        <div className="flex flex-col items-start gap-3">
          <p role="alert" className="text-sm text-error">
            {t('common.states.errorBody')}
          </p>
          <Button type="button" variant="outline" onClick={refetch}>
            {t('common.actions.retry')}
          </Button>
        </div>
      ) : (
        <>
          <div className="flex flex-wrap items-end gap-3">
            <div className="flex flex-col gap-1">
              <label htmlFor="approvals-filter-project" className="text-xs font-medium">
                {t('approvals.filters.project')}
              </label>
              <Select
                id="approvals-filter-project"
                value={projectFilter}
                onChange={(event) => setProjectFilter(event.target.value)}
              >
                <option value="">{t('approvals.filters.all')}</option>
                {projects.map((project) => (
                  <option key={project.id} value={project.id}>
                    {project.name}
                  </option>
                ))}
              </Select>
            </div>
            <div className="flex flex-col gap-1">
              <label htmlFor="approvals-filter-priority" className="text-xs font-medium">
                {t('approvals.filters.priority')}
              </label>
              <Select
                id="approvals-filter-priority"
                value={priorityFilter}
                onChange={(event) => setPriorityFilter(event.target.value as Priority | '')}
              >
                <option value="">{t('approvals.filters.all')}</option>
                {prioritySchema.options.map((priority) => (
                  <option key={priority} value={priority}>
                    {t(`status.priority.${priority}`)}
                  </option>
                ))}
              </Select>
            </div>
            <div className="flex flex-col gap-1">
              <label htmlFor="approvals-filter-due" className="text-xs font-medium">
                {t('approvals.filters.due')}
              </label>
              <Select
                id="approvals-filter-due"
                value={dueFilter}
                onChange={(event) => setDueFilter(event.target.value as DueFilter)}
              >
                <option value="all">{t('approvals.filters.dueAll')}</option>
                <option value="overdue">{t('approvals.filters.dueOverdue')}</option>
                <option value="week">{t('approvals.filters.dueWeek')}</option>
                <option value="none">{t('approvals.filters.dueNone')}</option>
              </Select>
            </div>
          </div>

          {queue.length === 0 ? (
            <Card>
              <CardContent className="flex flex-col items-center gap-3 p-6 text-center">
                <CheckCircle2 aria-hidden="true" className="size-8 text-foreground-muted" />
                <h2 className="font-heading text-lg font-semibold">
                  {t('approvals.empty.title')}
                </h2>
                <p className="text-sm text-foreground-muted">{t('approvals.empty.body')}</p>
              </CardContent>
            </Card>
          ) : (
            <ul className="flex flex-col gap-3" aria-label={t('approvals.queue.label')}>
              {queue.map((approval) => (
                <ApprovalItem
                  key={approval.id}
                  approval={approval}
                  project={projects.find((project) => project.id === approval.projectId)}
                  gate={gates.find((gate) => gate.id === approval.gateId)}
                  document={documents.find((doc) => doc.id === approval.documentId)}
                  task={tasks.find((task) => task.id === approval.taskId)}
                  now={now}
                />
              ))}
            </ul>
          )}
        </>
      )}
    </div>
  );
}
