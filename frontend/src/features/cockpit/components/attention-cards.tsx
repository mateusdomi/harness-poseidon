import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';

import type { Approval, Task } from '@/api';
import { Badge, Card, CardContent, CardHeader, CardTitle } from '@/design-system';
import { priorityVariant } from '@/lib/status';

/** Tarefas bloqueadas com motivo — clicáveis, abrem a tarefa no quadro. */
export function BlockedTasksCard({ tasks }: { tasks: Task[] }) {
  const { t } = useTranslation();
  const blocked = tasks.filter((task) => task.state === 'blocked');

  return (
    <Card>
      <CardHeader>
        <div className="flex items-center gap-2">
          <CardTitle>{t('cockpit.blocked.title')}</CardTitle>
          <Badge variant={blocked.length > 0 ? 'error' : 'outline'}>{blocked.length}</Badge>
        </div>
      </CardHeader>
      <CardContent>
        {blocked.length === 0 ? (
          <p className="text-sm text-foreground-muted">{t('cockpit.blocked.empty')}</p>
        ) : (
          <ul className="flex flex-col gap-2">
            {blocked.map((task) => (
              <li key={task.id}>
                <Link
                  to={`/board?task=${task.id}`}
                  className="flex min-h-touch flex-col justify-center gap-1 rounded-md border border-border p-3 transition-colors hover:bg-surface-elevated focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                >
                  <span className="flex flex-wrap items-center gap-2">
                    <span className="text-sm font-medium">{task.title}</span>
                    <Badge variant={priorityVariant(task.priority)}>
                      {t(`status.priority.${task.priority}`)}
                    </Badge>
                  </span>
                  <span className="text-xs text-error">{task.blockedReason}</span>
                </Link>
              </li>
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  );
}

/** Aprovações pendentes — cada item e o rodapé levam a /approvals. */
export function PendingApprovalsCard({ approvals }: { approvals: Approval[] }) {
  const { t } = useTranslation();
  const pending = approvals.filter((approval) => approval.state === 'pending');

  return (
    <Card>
      <CardHeader>
        <div className="flex items-center gap-2">
          <CardTitle>{t('cockpit.approvals.title')}</CardTitle>
          <Badge variant={pending.length > 0 ? 'warning' : 'outline'}>{pending.length}</Badge>
        </div>
      </CardHeader>
      <CardContent className="flex flex-col gap-3">
        {pending.length === 0 ? (
          <p className="text-sm text-foreground-muted">{t('cockpit.approvals.empty')}</p>
        ) : (
          <ul className="flex flex-col gap-2">
            {pending.map((approval) => (
              <li key={approval.id}>
                <Link
                  to="/approvals"
                  className="flex min-h-touch flex-col justify-center gap-1 rounded-md border border-border p-3 transition-colors hover:bg-surface-elevated focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                >
                  <span className="text-sm font-medium">{approval.title}</span>
                  <span className="text-xs text-foreground-muted">{approval.description}</span>
                </Link>
              </li>
            ))}
          </ul>
        )}
        <Link
          to="/approvals"
          className="self-start text-sm font-medium text-brand-strong underline-offset-4 hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
        >
          {t('cockpit.approvals.viewAll')}
        </Link>
      </CardContent>
    </Card>
  );
}
