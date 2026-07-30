import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';

import { TASK_STATES, type TaskState } from '@/api';
import { Card, CardContent, CardHeader, CardTitle } from '@/design-system';
import { formatNumber } from '@/lib/format';
import { cn } from '@/lib/utils';

const DOT: Record<TaskState, string> = {
  backlog: 'bg-foreground-muted',
  ready: 'bg-foreground',
  development: 'bg-info',
  review: 'bg-warning',
  corrections: 'bg-warning',
  testsGates: 'bg-brand',
  blocked: 'bg-error',
  done: 'bg-success',
};

interface TaskStateCountersProps {
  counts: Record<TaskState, number>;
}

/** Contadores por coluna — clicáveis, navegam para o quadro filtrado. */
export function TaskStateCounters({ counts }: TaskStateCountersProps) {
  const { t } = useTranslation();
  const navigate = useNavigate();

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t('cockpit.tasksByState.title')}</CardTitle>
      </CardHeader>
      <CardContent>
        <ul className="grid grid-cols-2 gap-2 md:grid-cols-4">
          {TASK_STATES.map((state) => (
            <li key={state}>
              <button
                type="button"
                onClick={() => navigate(`/board?state=${state}`)}
                aria-label={t('cockpit.tasksByState.open', {
                  state: t(`status.taskState.${state}`),
                  count: counts[state],
                })}
                className={cn(
                  'flex min-h-touch w-full flex-col items-start gap-1 rounded-md border border-border bg-surface-elevated/40 p-3 text-left',
                  'transition-colors hover:bg-surface-elevated focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent',
                )}
                data-state={state}
              >
                <span className="flex items-center gap-2 text-xs text-foreground-muted">
                  <span aria-hidden="true" className={cn('size-2 rounded-full', DOT[state])} />
                  {t(`status.taskState.${state}`)}
                </span>
                <span className="font-heading text-xl font-semibold tabular-nums">
                  {formatNumber(counts[state])}
                </span>
              </button>
            </li>
          ))}
        </ul>
      </CardContent>
    </Card>
  );
}
