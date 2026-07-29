import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { CheckCircle2 } from 'lucide-react';

import { prioritySchema, type Priority, type Ulid } from '@/api';
import { Button, Card, CardContent, Select, Skeleton } from '@/design-system';
import { ApprovalItem } from '@/features/approvals/components/approval-item';
import { useApprovalsQueue, useApprovalsRealtime } from '@/features/approvals/hooks/use-approvals';
import {
  matchesDueFilter,
  sortQueue,
  type DueFilter,
} from '@/features/approvals/lib/approvals-derive';
import { useNow } from '@/features/board/hooks/use-board';
import { PaginationBar } from '@/features/shared/components/pagination';
import { usePagination } from '@/features/shared/hooks/use-pagination';

export interface ApprovalInboxProps {
  /** Projeto ativo: a aba mostra só o que espera decisão NESTE projeto. */
  projectId: Ulid;
}

/**
 * "Aguardando sua aprovação" — a aba onde a decisão acontece (D9).
 *
 * A tela de Aprovações e a de Documentos sempre operaram o mesmo dado, e ter as
 * duas obrigava o dono a descobrir sozinho que "aprovar" morava em outro lugar.
 * Aqui a fila vive dentro de Documentos: mesma ordenação (prazo → criticidade →
 * mais antigo), mesmo item com impacto e evidências, e a ação a um clique.
 *
 * A fila continua trazendo TODO tipo de decisão do projeto — documento, portão,
 * tarefa, escolha de plano — porque esconder do dono um portão pendente só
 * porque ele não é documento seria pior que a tela extra que a fusão eliminou.
 * O filtro de projeto saiu: quem manda é a seleção global do cabeçalho.
 */
export function ApprovalInbox({ projectId }: ApprovalInboxProps) {
  const { t } = useTranslation();
  const { approvals, projects, gates, documents, tasks, isPending, isError, refetch } =
    useApprovalsQueue();
  useApprovalsRealtime([projectId]);
  const now = useNow();

  const [priorityFilter, setPriorityFilter] = useState<Priority | ''>('');
  const [dueFilter, setDueFilter] = useState<DueFilter>('all');

  const queue = useMemo(() => {
    let result = approvals.filter(
      (approval) => approval.state === 'pending' && approval.projectId === projectId,
    );
    if (priorityFilter !== '') {
      result = result.filter((approval) => approval.priority === priorityFilter);
    }
    result = result.filter((approval) => matchesDueFilter(approval, dueFilter, now));
    return sortQueue(result);
  }, [approvals, projectId, priorityFilter, dueFilter, now]);

  const pagination = usePagination(queue.length, {
    resetKey: `${projectId}:${priorityFilter}:${dueFilter}`,
  });

  if (isPending) {
    return (
      <div className="flex flex-col gap-3" role="status" aria-label={t('common.states.loading')}>
        <Skeleton className="h-10 w-full" />
        {Array.from({ length: 3 }, (_, index) => (
          <Skeleton key={index} className="h-32 w-full" />
        ))}
      </div>
    );
  }

  if (isError) {
    return (
      <div className="flex flex-col items-start gap-3">
        <p role="alert" className="text-sm text-error">
          {t('common.states.errorBody')}
        </p>
        <Button type="button" variant="outline" onClick={refetch}>
          {t('common.actions.retry')}
        </Button>
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-4">
      <p className="text-sm text-foreground-muted">{t('documents.approvalsTab.intro')}</p>

      <div className="flex flex-wrap items-end gap-3">
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
        <span className="text-sm text-foreground-muted">
          {t('approvals.queue.count', { count: queue.length })}
        </span>
      </div>

      {queue.length === 0 ? (
        <Card>
          <CardContent className="flex flex-col items-center gap-3 p-6 text-center">
            <CheckCircle2 aria-hidden="true" className="size-8 text-foreground-muted" />
            <h2 className="font-heading text-lg font-semibold">{t('approvals.empty.title')}</h2>
            <p className="text-sm text-foreground-muted">{t('approvals.empty.body')}</p>
          </CardContent>
        </Card>
      ) : (
        <>
          <ul className="flex flex-col gap-3" aria-label={t('approvals.queue.label')}>
            {pagination.paginate(queue).map((approval) => (
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
          <PaginationBar pagination={pagination} />
        </>
      )}
    </div>
  );
}
