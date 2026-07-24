import { useTranslation } from 'react-i18next';

import { Card, CardContent, CardHeader, CardTitle } from '@/design-system';
import { cn } from '@/lib/utils';

import type { DeliveryFeatureMetric, DeliveryForecast } from '../api/types';

/**
 * Gráficos da Entrega 360 — SVG/HTML puro (sem rede/CDN, respeita a CSP),
 * seguindo o padrão de `tasks-by-state-chart`: uma escala por gráfico, marcas
 * finas, rótulos diretos e cores semânticas do design system (claro/escuro via
 * tokens). SÓ grafica dado real da API — cada gráfico tem empty-state honesto.
 */
export function DeliveryCharts({
  features,
  milestonesDone,
  milestonesTotal,
  forecastHistory,
}: {
  features: readonly DeliveryFeatureMetric[];
  milestonesDone: number;
  milestonesTotal: number;
  forecastHistory: readonly DeliveryForecast[];
}) {
  return (
    <div className="grid gap-6 lg:grid-cols-2">
      <MilestonesMeter done={milestonesDone} total={milestonesTotal} />
      <ForecastConfidenceTrend history={forecastHistory} />
      <FeatureOutcomesChart features={features} />
    </div>
  );
}

/**
 * Progresso de marcos — medidor de série única (concluídos / total). Barra
 * direta com o percentual rotulado; a trilha é o restante. Sem legenda.
 */
function MilestonesMeter({ done, total }: { done: number; total: number }) {
  const { t } = useTranslation();
  const pct = total > 0 ? Math.round((done / total) * 100) : 0;
  return (
    <Card>
      <CardHeader>
        <CardTitle>{t('delivery.charts.milestones.title')}</CardTitle>
        <p className="text-xs text-foreground-muted">
          {t('delivery.charts.milestones.caption', { done, total })}
        </p>
      </CardHeader>
      <CardContent>
        {total === 0 ? (
          <p className="text-sm text-foreground-muted">{t('delivery.charts.milestones.empty')}</p>
        ) : (
          <div className="flex items-center gap-3">
            <span
              aria-hidden="true"
              className="flex h-4 flex-1 items-center overflow-hidden rounded-full bg-surface-elevated"
            >
              <span
                className={cn('h-full rounded-full bg-success transition-[width]', done > 0 && 'min-w-1')}
                style={{ width: `${pct}%` }}
              />
            </span>
            <span
              className="text-lg font-semibold tabular-nums text-foreground"
              aria-label={t('delivery.charts.milestones.aria', { done, total, pct })}
            >
              {pct}%
            </span>
          </div>
        )}
      </CardContent>
    </Card>
  );
}

/**
 * Tentativas por feature — barras horizontais empilhadas (sucesso × falha) numa
 * única escala (total de tentativas). Duas séries de estado → legenda + rótulos
 * diretos das contagens. Gap de 2px entre os segmentos (borda da superfície).
 */
function FeatureOutcomesChart({ features }: { features: readonly DeliveryFeatureMetric[] }) {
  const { t } = useTranslation();
  const rows = features.filter((f) => f.successCount + f.failureCount > 0);
  const max = Math.max(1, ...rows.map((f) => f.successCount + f.failureCount));

  return (
    <Card className="lg:col-span-2">
      <CardHeader>
        <CardTitle>{t('delivery.charts.features.title')}</CardTitle>
        <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-foreground-muted">
          <span className="inline-flex items-center gap-1.5">
            <span aria-hidden="true" className="size-2.5 rounded-full bg-success" />
            {t('delivery.charts.features.success')}
          </span>
          <span className="inline-flex items-center gap-1.5">
            <span aria-hidden="true" className="size-2.5 rounded-full bg-error" />
            {t('delivery.charts.features.failure')}
          </span>
        </div>
      </CardHeader>
      <CardContent>
        {rows.length === 0 ? (
          <p className="text-sm text-foreground-muted">{t('delivery.charts.features.empty')}</p>
        ) : (
          <ul className="flex flex-col gap-2.5">
            {rows.map((f) => {
              const total = f.successCount + f.failureCount;
              return (
                <li
                  key={f.featureId}
                  className="grid grid-cols-[9rem_1fr_4rem] items-center gap-3"
                  aria-label={t('delivery.charts.features.bar', {
                    feature: f.featureId,
                    success: f.successCount,
                    failure: f.failureCount,
                  })}
                >
                  <span className="truncate text-xs text-foreground-muted" title={f.featureId}>
                    {f.featureId}
                  </span>
                  <span
                    aria-hidden="true"
                    className="flex h-4 items-center gap-0.5 overflow-hidden rounded-full bg-surface-elevated"
                    style={{ width: `${Math.round((total / max) * 100)}%` }}
                  >
                    <span
                      className={cn('h-full bg-success', f.successCount > 0 && 'min-w-1')}
                      style={{ width: `${(f.successCount / total) * 100}%` }}
                    />
                    <span
                      className={cn('h-full bg-error', f.failureCount > 0 && 'min-w-1')}
                      style={{ width: `${(f.failureCount / total) * 100}%` }}
                    />
                  </span>
                  <span className="text-right text-sm font-semibold tabular-nums text-foreground">
                    <span className="text-success">{f.successCount}</span>
                    <span className="text-foreground-muted"> / </span>
                    <span className={f.failureCount > 0 ? 'text-error' : 'text-foreground-muted'}>
                      {f.failureCount}
                    </span>
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

/**
 * Tendência de confiança da previsão — série única ao longo do tempo (0–100%),
 * linha + área com marcas nos pontos. Requer ≥ 2 registros de histórico; abaixo
 * disso, empty-state honesto (uma medição não é tendência). Escala Y fixa 0–100.
 */
function ForecastConfidenceTrend({ history }: { history: readonly DeliveryForecast[] }) {
  const { t } = useTranslation();
  // Ordena do mais antigo ao mais recente pelo createdAt (registros sem data ao fim).
  const points = [...history]
    .filter((h) => h.createdAt !== null)
    .sort((a, b) => Date.parse(a.createdAt!) - Date.parse(b.createdAt!));

  const W = 320;
  const H = 120;
  const PAD = 12;
  const innerW = W - PAD * 2;
  const innerH = H - PAD * 2;

  const coords = points.map((p, index) => {
    const x = points.length === 1 ? PAD + innerW / 2 : PAD + (index / (points.length - 1)) * innerW;
    const y = PAD + (1 - Math.min(100, Math.max(0, p.confidencePercent)) / 100) * innerH;
    return { x, y, pct: p.confidencePercent, at: p.createdAt! };
  });

  const linePath = coords.map((c) => `${c.x.toFixed(1)},${c.y.toFixed(1)}`).join(' ');
  const areaPath =
    coords.length > 0
      ? `M ${coords[0]!.x.toFixed(1)} ${(H - PAD).toFixed(1)} ` +
        coords.map((c) => `L ${c.x.toFixed(1)} ${c.y.toFixed(1)}`).join(' ') +
        ` L ${coords[coords.length - 1]!.x.toFixed(1)} ${(H - PAD).toFixed(1)} Z`
      : '';
  const last = coords[coords.length - 1];

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t('delivery.charts.forecast.title')}</CardTitle>
        <p className="text-xs text-foreground-muted">{t('delivery.charts.forecast.caption')}</p>
      </CardHeader>
      <CardContent>
        {coords.length < 2 ? (
          <p className="text-sm text-foreground-muted">{t('delivery.charts.forecast.empty')}</p>
        ) : (
          <svg
            viewBox={`0 0 ${W} ${H}`}
            className="h-32 w-full text-brand"
            role="img"
            aria-label={t('delivery.charts.forecast.aria', {
              from: coords[0]!.pct,
              to: last!.pct,
              count: coords.length,
            })}
          >
            {/* Grade recessiva: 0/50/100% */}
            {[0, 50, 100].map((g) => {
              const y = PAD + (1 - g / 100) * innerH;
              return (
                <line
                  key={g}
                  x1={PAD}
                  x2={W - PAD}
                  y1={y}
                  y2={y}
                  className="stroke-border"
                  strokeWidth={1}
                />
              );
            })}
            <path d={areaPath} className="fill-brand/10" />
            <polyline
              points={linePath}
              fill="none"
              className="stroke-brand"
              strokeWidth={2}
              strokeLinecap="round"
              strokeLinejoin="round"
            />
            {coords.map((c, index) => (
              <circle key={index} cx={c.x} cy={c.y} r={3} className="fill-brand">
                <title>{`${c.pct}%`}</title>
              </circle>
            ))}
            {/* Rótulo direto do ponto mais recente */}
            <text
              x={last!.x}
              y={Math.max(PAD + 8, last!.y - 6)}
              textAnchor="end"
              className="fill-foreground text-[11px] font-semibold"
            >
              {last!.pct}%
            </text>
          </svg>
        )}
      </CardContent>
    </Card>
  );
}
