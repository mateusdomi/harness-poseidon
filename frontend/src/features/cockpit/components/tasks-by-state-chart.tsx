import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';

import { TASK_STATES, type TaskState } from '@/api';
import { Card, CardContent, CardHeader, CardTitle } from '@/design-system';
import { formatNumber } from '@/lib/format';
import { cn } from '@/lib/utils';

/**
 * Cor semântica por estado — a MESMA usada nos contadores (task-state-counters),
 * para o cockpit ler como um só sistema. Série única: cada barra é direta-
 * mente rotulada (estado + contagem), então não há legenda.
 */
const BAR_FILL: Record<TaskState, string> = {
  backlog: 'bg-foreground-muted',
  ready: 'bg-foreground',
  development: 'bg-info',
  review: 'bg-warning',
  corrections: 'bg-warning',
  testsGates: 'bg-brand',
  blocked: 'bg-error',
  done: 'bg-success',
};

/**
 * Gráfico de barras horizontais "tarefas por estado" — a primeira visualização
 * do sistema. SVG/HTML puro (sem rede/CDN, respeita a CSP): cada barra escala
 * pela maior contagem, com o número ancorado à direita. Foco de fábrica: onde
 * o trabalho está represado agora.
 */
export function TasksByStateChart({ counts }: { counts: Record<TaskState, number> }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const total = TASK_STATES.reduce((sum, state) => sum + counts[state], 0);
  const max = Math.max(1, ...TASK_STATES.map((state) => counts[state]));

  return (
    <Card className="lg:col-span-2">
      <CardHeader>
        <CardTitle>{t('cockpit.tasksChart.title')}</CardTitle>
        <p className="text-xs text-foreground-muted">{t('cockpit.tasksChart.subtitle')}</p>
      </CardHeader>
      <CardContent>
        {total === 0 ? (
          <p className="text-sm text-foreground-muted">{t('cockpit.tasksChart.empty')}</p>
        ) : (
          <ul className="flex flex-col gap-2.5">
            {TASK_STATES.map((state) => {
              const value = counts[state];
              const label = t(`status.taskState.${state}`);
              return (
                <li key={state}>
                  <button
                    type="button"
                    onClick={() => navigate(`/board?state=${state}`)}
                    aria-label={t('cockpit.tasksByState.open', { state: label, count: value })}
                    className="grid min-h-11 w-full grid-cols-[8rem_1fr_2rem] items-center gap-3 rounded-md px-1 text-left hover:bg-surface-elevated/60 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                  >
                    <span
                      className="truncate text-xs text-foreground-muted"
                    >
                      {label}
                    </span>
                    <span
                      aria-hidden="true"
                      className="flex h-4 items-center overflow-hidden rounded-full bg-surface-elevated"
                    >
                      <span
                        className={cn(
                          'h-full rounded-full transition-[width]',
                          value > 0 && 'min-w-1',
                          BAR_FILL[state],
                        )}
                        style={{ width: `${Math.round((value / max) * 100)}%` }}
                      />
                    </span>
                    <span className="text-right text-sm font-semibold tabular-nums">
                      {formatNumber(value)}
                    </span>
                  </button>
                </li>
              );
            })}
          </ul>
        )}
      </CardContent>
    </Card>
  );
}
