import { useTranslation } from 'react-i18next';

import {
  Badge,
  Button,
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  Skeleton,
} from '@/design-system';

import { formatDate, formatDateTime, formatUsd } from '../lib/format';
import { useForecast, useMetrics, useOverview, useRecalcForecast } from '../hooks/use-delivery';
import type { DeliveryForecast, DeliveryMetric } from '../api/types';
import { DeliveryCharts } from './delivery-charts';
import {
  ConfidenceBadge,
  HealthBadge,
  IndicatorStatusBadge,
  PredictabilityBadge,
  SeverityBadge,
} from './status-badges';

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt className="text-xs text-foreground-muted">{label}</dt>
      <dd className="text-sm font-medium text-foreground">{value}</dd>
    </div>
  );
}

function ForecastBlock({ forecast }: { forecast: DeliveryForecast }) {
  const { t, i18n } = useTranslation();
  const date = formatDate(forecast.forecastDate, i18n.language);
  return (
    <div className="flex flex-col gap-2">
      <h3 className="text-sm font-semibold text-foreground">{t('delivery.overview.plan.forecastTitle')}</h3>
      <div className="flex flex-wrap items-center gap-2">
        <span className="text-sm font-medium text-foreground">{t('delivery.overview.plan.forecastDate')}:</span>
        {forecast.hasSufficientEvidence && date ? (
          <span className="text-sm text-foreground">{date}</span>
        ) : (
          <span className="text-sm text-warning">{t('delivery.overview.plan.insufficient')}</span>
        )}
        <ConfidenceBadge value={forecast.confidence} />
        <Badge variant="outline">{forecast.confidencePercent}%</Badge>
      </div>
      {forecast.basis.length > 0 && (
        <div>
          <p className="text-xs font-medium text-foreground-muted">{t('delivery.overview.plan.basis')}</p>
          <ul className="mt-1 list-disc pl-5 text-xs text-foreground-muted">
            {forecast.basis.map((b) => (
              <li key={b.signal}>{b.detail}</li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}

function MetricTable({ title, metrics }: { title: string; metrics: DeliveryMetric[] }) {
  const { t } = useTranslation();
  return (
    <div className="flex flex-col gap-2">
      <h3 className="text-sm font-semibold text-foreground">{title}</h3>
      <div className="overflow-x-auto">
        <table className="w-full min-w-[28rem] border-collapse text-sm">
          <thead>
            <tr className="border-b border-border-strong text-left text-xs text-foreground-muted">
              <th className="py-1 pr-2 font-medium">{t('delivery.overview.metrics.columns.metric')}</th>
              <th className="py-1 pr-2 font-medium">{t('delivery.overview.metrics.columns.value')}</th>
              <th className="py-1 font-medium">{t('delivery.overview.metrics.columns.basis')}</th>
            </tr>
          </thead>
          <tbody>
            {metrics.map((m) => (
              <tr key={m.key} className="border-b border-border">
                <td className="py-1 pr-2 text-foreground">{m.label}</td>
                <td className="py-1 pr-2">
                  {m.measured ? (
                    <span className="font-medium text-foreground">
                      {m.value}
                      {m.unit ? ` ${m.unit}` : ''}
                    </span>
                  ) : (
                    <Badge variant="outline">{t('delivery.overview.metrics.notMeasured')}</Badge>
                  )}
                </td>
                <td className="py-1 text-xs text-foreground-muted">{m.basis}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

/**
 * Entrega 360 (DEL-02/06/09): resumo executivo, plano & marcos com histórico
 * de previsão (nunca sobrescrita), saúde técnica, riscos & dependências,
 * decisões, documentação, valor & métricas e as métricas DORA/próprias.
 */
export function DeliveryOverview({ deliveryId }: { deliveryId: string }) {
  const { t, i18n } = useTranslation();
  const overviewQuery = useOverview(deliveryId);
  const forecastQuery = useForecast(deliveryId);
  const metricsQuery = useMetrics(deliveryId);
  const recalc = useRecalcForecast(deliveryId);

  if (overviewQuery.isLoading) {
    return <Skeleton className="h-96 w-full" />;
  }
  if (overviewQuery.isError || !overviewQuery.data) {
    return (
      <Card>
        <CardContent className="py-8 text-center text-sm text-error">{t('delivery.error')}</CardContent>
      </Card>
    );
  }

  const o = overviewQuery.data;
  const e = o.executiveSummary;

  return (
    <div className="flex flex-col gap-6" data-testid="delivery-overview">
      <Card>
        <CardHeader className="flex flex-row flex-wrap items-center justify-between gap-3">
          <CardTitle>{t('delivery.overview.exec.title')}</CardTitle>
          <div className="flex items-center gap-2">
            <HealthBadge value={e.health} />
            <PredictabilityBadge value={e.predictability} />
          </div>
        </CardHeader>
        <CardContent>
          <dl className="grid grid-cols-2 gap-x-4 gap-y-3 sm:grid-cols-4">
            <Stat label={t('delivery.overview.exec.owner')} value={e.owner ?? t('delivery.portfolio.noOwner')} />
            <Stat label={t('delivery.criticality.label')} value={t(`delivery.criticality.${e.criticality}`)} />
            <Stat label={t('delivery.overview.exec.milestones')} value={`${e.milestonesDone}/${e.milestonesTotal}`} />
            <Stat label={t('delivery.overview.exec.openTasks')} value={String(e.openTaskCount)} />
            <Stat label={t('delivery.overview.exec.blockedTasks')} value={String(e.blockedTaskCount)} />
            <Stat label={t('delivery.overview.exec.committed')} value={formatDate(e.committedDate, i18n.language) ?? t('delivery.portfolio.noDate')} />
            <Stat label={t('delivery.overview.exec.forecast')} value={formatDate(e.forecastDate, i18n.language) ?? t('delivery.portfolio.noDate')} />
            <Stat label={t('delivery.overview.exec.lastActivity')} value={formatDateTime(e.lastActivityAt, i18n.language) ?? '—'} />
          </dl>
        </CardContent>
      </Card>

      <DeliveryCharts
        features={o.valueAndMetrics.features}
        milestonesDone={o.planAndMilestones.milestonesDone}
        milestonesTotal={o.planAndMilestones.milestonesTotal}
        forecastHistory={forecastQuery.data?.history ?? o.planAndMilestones.forecastHistory}
      />

      <div className="grid gap-6 lg:grid-cols-2">
        <Card>
          <CardHeader className="flex flex-row items-center justify-between gap-2">
            <CardTitle>{t('delivery.overview.plan.title')}</CardTitle>
            <Button
              variant="outline"
              size="sm"
              onClick={() => recalc.mutate()}
              disabled={recalc.isPending}
            >
              {t('delivery.overview.plan.recalc')}
            </Button>
          </CardHeader>
          <CardContent className="flex flex-col gap-4">
            <ForecastBlock forecast={o.planAndMilestones.forecast} />
            {(forecastQuery.data?.history.length ?? 0) > 0 && (
              <div>
                <p className="text-xs font-medium text-foreground-muted">{t('delivery.overview.plan.history')}</p>
                <ul className="mt-1 flex flex-col gap-1 text-xs">
                  {forecastQuery.data!.history.map((h, index) => (
                    <li key={h.id ?? index} className="flex items-center gap-2 text-foreground-muted">
                      <span>{formatDate(h.createdAt, i18n.language) ?? '—'}</span>
                      <span aria-hidden>→</span>
                      <span className="text-foreground">
                        {formatDate(h.forecastDate, i18n.language) ?? t('delivery.overview.plan.noDate')}
                      </span>
                      <ConfidenceBadge value={h.confidence} />
                    </li>
                  ))}
                </ul>
              </div>
            )}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>{t('delivery.overview.technical.title')}</CardTitle>
          </CardHeader>
          <CardContent>
            {o.technicalHealth.indicators.length === 0 ? (
              <p className="text-sm text-foreground-muted">{t('delivery.overview.technical.empty')}</p>
            ) : (
              <ul className="flex flex-col gap-2">
                {o.technicalHealth.indicators.map((ind) => (
                  <li key={ind.key} className="flex items-center justify-between gap-2 text-sm">
                    <span className="text-foreground">{ind.label}</span>
                    <span className="flex items-center gap-2">
                      <span className="text-foreground-muted">{ind.value}</span>
                      <IndicatorStatusBadge value={ind.status} />
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>{t('delivery.overview.risks.title')}</CardTitle>
          </CardHeader>
          <CardContent className="flex flex-col gap-3">
            <div className="flex gap-4 text-sm">
              <Stat label={t('delivery.overview.risks.openDependencies')} value={String(o.risksAndDependencies.openDependencies)} />
              <Stat label={t('delivery.overview.risks.blockedTasks')} value={String(o.risksAndDependencies.blockedTaskCount)} />
            </div>
            {o.risksAndDependencies.risks.length === 0 ? (
              <p className="text-sm text-foreground-muted">{t('delivery.overview.risks.empty')}</p>
            ) : (
              <ul className="flex flex-col gap-2">
                {o.risksAndDependencies.risks.map((r) => (
                  <li key={r.code} className="flex items-start gap-2 text-sm">
                    <SeverityBadge value={r.severity} />
                    <span className="text-foreground-muted">{r.detail}</span>
                  </li>
                ))}
              </ul>
            )}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>{t('delivery.overview.decisions.title')}</CardTitle>
          </CardHeader>
          <CardContent className="flex flex-col gap-3">
            <div className="flex gap-4 text-sm">
              <Stat label={t('delivery.overview.decisions.total')} value={String(o.decisions.total)} />
              <Stat label={t('delivery.overview.decisions.open')} value={String(o.decisions.open)} />
            </div>
            {o.decisions.decisions.length === 0 ? (
              <p className="text-sm text-foreground-muted">{t('delivery.overview.decisions.empty')}</p>
            ) : (
              <ul className="flex flex-col gap-2 text-sm">
                {o.decisions.decisions.map((d) => (
                  <li key={d.taskId} className="flex items-start gap-2">
                    <Badge variant={d.resolved ? 'success' : 'outline'}>
                      {d.resolved ? t('delivery.overview.decisions.resolved') : t('delivery.overview.decisions.unresolved')}
                    </Badge>
                    <span className="text-foreground-muted">{d.detail}</span>
                  </li>
                ))}
              </ul>
            )}
          </CardContent>
        </Card>

        <Card>
          <CardHeader className="flex flex-row items-center justify-between gap-2">
            <CardTitle>{t('delivery.overview.docs.title')}</CardTitle>
            <Badge variant="outline">
              {t('delivery.overview.docs.coverage', {
                present: o.documentation.present,
                expected: o.documentation.expected,
              })}
            </Badge>
          </CardHeader>
          <CardContent>
            <ul className="flex flex-col gap-2 text-sm">
              {o.documentation.checklist.map((item) => (
                <li key={item.kind} className="flex items-center justify-between gap-2">
                  <span className="text-foreground">{item.label}</span>
                  <Badge variant={item.present ? 'success' : 'error'}>
                    {item.present ? t('delivery.overview.docs.present') : t('delivery.overview.docs.missing')}
                  </Badge>
                </li>
              ))}
            </ul>
          </CardContent>
        </Card>

        <Card>
          <CardHeader className="flex flex-row items-center justify-between gap-2">
            <CardTitle>{t('delivery.overview.value.title')}</CardTitle>
            <span className="text-sm text-foreground-muted">
              {formatUsd(o.valueAndMetrics.totalCostUsd, i18n.language)} · {o.valueAndMetrics.totalTasks} {t('delivery.overview.value.totalTasks')}
            </span>
          </CardHeader>
          <CardContent>
            {o.valueAndMetrics.features.length === 0 ? (
              <p className="text-sm text-foreground-muted">{t('delivery.overview.value.empty')}</p>
            ) : (
              <div className="overflow-x-auto">
                <table className="w-full min-w-[28rem] border-collapse text-sm">
                  <thead>
                    <tr className="border-b border-border-strong text-left text-xs text-foreground-muted">
                      <th className="py-1 pr-2 font-medium">{t('delivery.overview.value.feature')}</th>
                      <th className="py-1 pr-2 font-medium">{t('delivery.overview.value.tasks')}</th>
                      <th className="py-1 pr-2 font-medium">{t('delivery.overview.value.success')}</th>
                      <th className="py-1 pr-2 font-medium">{t('delivery.overview.value.failures')}</th>
                      <th className="py-1 font-medium">{t('delivery.overview.value.cost')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {o.valueAndMetrics.features.map((f) => (
                      <tr key={f.featureId} className="border-b border-border">
                        <td className="py-1 pr-2 text-foreground">{f.featureId}</td>
                        <td className="py-1 pr-2 text-foreground-muted">{f.taskCount}</td>
                        <td className="py-1 pr-2 text-foreground-muted">{f.successCount}</td>
                        <td className="py-1 pr-2 text-foreground-muted">{f.failureCount}</td>
                        <td className="py-1 text-foreground-muted">{formatUsd(f.totalCostUsd, i18n.language)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </CardContent>
        </Card>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>{t('delivery.overview.metrics.title')}</CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col gap-6">
          {metricsQuery.isLoading ? (
            <Skeleton className="h-24 w-full" />
          ) : metricsQuery.data ? (
            <>
              <MetricTable title={t('delivery.overview.metrics.dora')} metrics={metricsQuery.data.dora} />
              <MetricTable title={t('delivery.overview.metrics.own')} metrics={metricsQuery.data.own} />
            </>
          ) : null}
        </CardContent>
      </Card>
    </div>
  );
}
