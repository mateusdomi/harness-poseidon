import { useTranslation } from 'react-i18next';

import type { Agent } from '@/api';
import { Card, CardContent, CardHeader, CardTitle } from '@/design-system';
import { formatNumber } from '@/lib/format';
import { cn } from '@/lib/utils';

/**
 * Utilização da equipe — entregas concluídas por agente (`metrics.tasksCompleted`,
 * dado real do contrato). Barras horizontais de série única na MESMA escala
 * (maior contagem), cada barra diretamente rotulada com o nome e o total, então
 * sem legenda. SVG/HTML puro (sem rede/CDN, respeita a CSP), cores semânticas do
 * design system e tema claro/escuro via tokens — o mesmo padrão do cockpit.
 */
export function AgentsUtilizationChart({ agents }: { agents: readonly Agent[] }) {
  const { t } = useTranslation();
  // Ordena por produção (desc); nomes estáveis em empate para layout previsível.
  const rows = [...agents].sort(
    (a, b) =>
      b.metrics.tasksCompleted - a.metrics.tasksCompleted || a.name.localeCompare(b.name),
  );
  const total = rows.reduce((sum, agent) => sum + agent.metrics.tasksCompleted, 0);
  const max = Math.max(1, ...rows.map((agent) => agent.metrics.tasksCompleted));

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t('agents.chart.title')}</CardTitle>
        <p className="text-xs text-foreground-muted">{t('agents.chart.subtitle')}</p>
      </CardHeader>
      <CardContent>
        {rows.length === 0 || total === 0 ? (
          <p className="text-sm text-foreground-muted">{t('agents.chart.empty')}</p>
        ) : (
          <ul className="flex flex-col gap-2.5">
            {rows.map((agent) => {
              const value = agent.metrics.tasksCompleted;
              return (
                <li
                  key={agent.id}
                  className="grid grid-cols-[9rem_1fr_2.5rem] items-center gap-3"
                  aria-label={t('agents.chart.bar', { agent: agent.name, count: value })}
                >
                  <span className="truncate text-xs text-foreground-muted" title={agent.name}>
                    {agent.name}
                  </span>
                  <span
                    aria-hidden="true"
                    className="flex h-4 items-center overflow-hidden rounded-full bg-surface-elevated"
                  >
                    <span
                      className={cn(
                        'h-full rounded-full bg-brand transition-[width]',
                        value > 0 && 'min-w-1',
                      )}
                      style={{ width: `${Math.round((value / max) * 100)}%` }}
                    />
                  </span>
                  <span className="text-right text-sm font-semibold tabular-nums text-foreground">
                    {formatNumber(value)}
                  </span>
                </li>
              );
            })}
          </ul>
        )}
      </CardContent>
    </Card>
  );
}
