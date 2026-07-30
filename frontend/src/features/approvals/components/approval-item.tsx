import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { ChevronDown, ChevronUp } from 'lucide-react';

import type { Approval, Document, Gate, Project, Task } from '@/api';
import { Badge, Button } from '@/design-system';
import { formatDateTime, formatRelativeTime } from '@/lib/format';
import {
  documentStateVariant,
  gateStateVariant,
  priorityVariant,
  taskStateVariant,
} from '@/lib/status';
import { approvalKind } from '@/features/approvals/lib/approvals-derive';
import { ApprovalResolveActions } from '@/features/shared/components/approval-resolve-actions';
import { useResolveQueueApproval } from '@/features/approvals/hooks/use-approvals';

export interface ApprovalItemProps {
  approval: Approval;
  project: Project | undefined;
  gate: Gate | undefined;
  document: Document | undefined;
  task: Task | undefined;
  now: Date;
}

/**
 * Item da fila consolidada: tipo (gate/documento/tarefa/decisão),
 * criticidade, prazo, projeto e ações embutidas. O contexto expandido
 * mostra impacto e evidências (entidade relacionada com estado e link).
 */
export function ApprovalItem({ approval, project, gate, document, task, now }: ApprovalItemProps) {
  const { t, i18n } = useTranslation();
  const resolve = useResolveQueueApproval();
  const [expanded, setExpanded] = useState(false);

  const kind = approvalKind(approval);
  const overdue =
    approval.dueAt !== null && new Date(approval.dueAt).getTime() < now.getTime();

  return (
    <li className="flex flex-col gap-3 rounded-xl border border-border bg-surface p-4">
      <div className="flex flex-wrap items-center gap-2">
        <Badge variant="brand">{t(`approvals.kind.${kind}`)}</Badge>
        <Badge variant={priorityVariant(approval.priority)}>
          {t(`status.priority.${approval.priority}`)}
        </Badge>
        {approval.dueAt !== null && (
          <Badge variant={overdue ? 'error' : 'outline'}>
            {overdue
              ? t('approvals.item.overdue', { when: formatRelativeTime(approval.dueAt, i18n.language, now) })
              : t('approvals.item.due', { when: formatRelativeTime(approval.dueAt, i18n.language, now) })}
          </Badge>
        )}
        <span className="ml-auto text-xs text-foreground-muted">
          {project?.name ?? approval.projectId}
        </span>
      </div>

      <div className="flex flex-col gap-1">
        <h3 className="text-sm font-semibold">{approval.title}</h3>
        <p className="text-xs text-foreground-muted">{approval.description}</p>
        <p className="text-xs text-foreground-muted">
          {t('approvals.item.requestedAt', {
            when: formatDateTime(approval.requestedAt, i18n.language),
          })}
        </p>
      </div>

      <ApprovalResolveActions
        approval={approval}
        isPending={resolve.isPending}
        onResolve={(input) => resolve.mutate({ approvalId: approval.id, input })}
      />

      <div>
        <Button
          type="button"
          variant="ghost"
          size="sm"
          aria-expanded={expanded}
          onClick={() => setExpanded((current) => !current)}
        >
          {expanded ? <ChevronUp aria-hidden="true" /> : <ChevronDown aria-hidden="true" />}
          {expanded ? t('approvals.item.hideContext') : t('approvals.item.showContext')}
        </Button>

        {expanded && (
          <div className="mt-2 flex flex-col gap-2 rounded-lg border border-border p-3 text-xs">
            <span className="font-medium">{t('approvals.item.contextTitle')}</span>
            {kind === 'gate' && gate && (
              <span className="flex flex-wrap items-center gap-2">
                {gate.name}
                <Badge variant={gateStateVariant(gate.state)}>
                  {t(`status.gateState.${gate.state}`)}
                </Badge>
                <Link
                  to="/workflows"
                  className="min-h-11 text-brand-strong underline-offset-4 hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent md:min-h-0"
                >
                  {t('approvals.item.openWorkflow')}
                </Link>
              </span>
            )}
            {kind === 'document' && document && (
              <span className="flex flex-wrap items-center gap-2">
                {document.title}
                <Badge variant={documentStateVariant(document.state)}>
                  {t(`status.documentState.${document.state}`)}
                </Badge>
                <Link
                  to={`/documents?doc=${document.id}`}
                  className="min-h-11 text-brand-strong underline-offset-4 hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent md:min-h-0"
                >
                  {t('approvals.item.openDocument')}
                </Link>
              </span>
            )}
            {kind === 'task' && task && (
              <span className="flex flex-wrap items-center gap-2">
                {task.title}
                <Badge variant={taskStateVariant(task.state)}>
                  {t(`status.taskState.${task.state}`)}
                </Badge>
                <Link
                  to={`/board?task=${task.id}`}
                  className="min-h-11 text-brand-strong underline-offset-4 hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent md:min-h-0"
                >
                  {t('approvals.item.openTask')}
                </Link>
              </span>
            )}
            {kind === 'decision' && (
              <span className="text-foreground-muted">{t('approvals.item.decisionContext')}</span>
            )}
          </div>
        )}
      </div>
    </li>
  );
}
