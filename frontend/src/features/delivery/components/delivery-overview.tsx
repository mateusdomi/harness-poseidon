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
import { attentionSignalLabel } from '../lib/attention-signal';
import {
  useDeliveryTraceability,
  useForecast,
  useMetrics,
  useOverview,
  useRecalcForecast,
} from '../hooks/use-delivery';
import type { DeliveryForecast, DeliveryMetric } from '../api/types';
import { DeliveryCharts } from './delivery-charts';
import { SourceDisclosure } from './source-disclosure';
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

function publicDeliveryText(value: string): string {
  return value
    .replace(/milestones complete/gi, 'marcos concluídos')
    .replace(/open dependencies/gi, 'dependências abertas')
    .replace(/pending validations/gi, 'validações pendentes')
    .replace(/no completed-milestone variance history yet/gi, 'ainda não há histórico de variação de marcos concluídos')
    .replace(/average recorded variance ([+-]?[0-9.]+) day\(s\)/gi, 'variação média registrada de $1 dia(s)')
    .replace(/no committed date recorded/gi, 'nenhuma data comprometida registrada')
    .replace(/committed date ([0-9-]+)/gi, 'data comprometida $1')
    .replace(/no plan milestones to project a delivery date from/gi, 'não há marcos de plano para projetar uma data de entrega')
    .replace(/no committed date to anchor a forecast on/gi, 'não há data comprometida para ancorar a previsão');
}

const METRIC_LABELS: Readonly<Record<string, string>> = {
  change_lead_time: 'Tempo de ciclo da mudança',
  deployment_frequency: 'Frequência de implantação',
  failed_deploy_recovery: 'Tempo de recuperação de implantação com falha',
  change_fail_rate: 'Taxa de falha de mudança',
  deploy_rework: 'Retrabalho de implantação',
  forecast_accuracy: 'Acurácia da previsão',
};

const METRIC_BASIS: Readonly<Record<string, string>> = {
  change_lead_time:
    'Não há timestamps por mudança entre commit e implantação; o tempo de ciclo não pode ser derivado.',
  deployment_frequency:
    'Não há eventos de implantação com timestamps; a frequência ao longo do tempo não pode ser derivada.',
  failed_deploy_recovery:
    'Não há timestamps de falha e recuperação de implantação; o tempo de recuperação não pode ser derivado.',
  change_fail_rate: 'Calculada a partir das tentativas de execução registradas e suas falhas.',
  deploy_rework: 'Calculado a partir de itens que exigiram mais de uma tentativa registrada.',
  forecast_accuracy:
    'Exige uma data de entrega realizada para comparar com uma previsão datada.',
  documentation_coverage: 'Calculada a partir dos documentos esperados presentes.',
  homologation_defects: 'Contagem de tarefas atualmente no estado de correção.',
  scope_changes: 'Contagem de solicitações que substituíram uma solicitação anterior.',
  open_dependencies: 'Contagem de tarefas abertas bloqueadas por dependência não resolvida.',
  time_waiting_access:
    'Contagem de tarefas abertas aguardando acesso; a duração da espera não é registrada.',
  planned_vs_realized_value: 'Relação entre marcos planejados e marcos realizados.',
};

const METRIC_UNITS: Readonly<Record<string, string>> = {
  count: 'itens',
  milestones: 'marcos',
  days: 'dias',
};

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
              <li key={b.signal}>{publicDeliveryText(b.detail)}</li>
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
                <td className="py-1 pr-2 text-foreground">{METRIC_LABELS[m.key] ?? m.label}</td>
                <td className="py-1 pr-2">
                  {m.measured ? (
                    <span className="font-medium text-foreground">
                      {m.value}
                      {m.unit ? ` ${METRIC_UNITS[m.unit] ?? m.unit}` : ''}
                    </span>
                  ) : (
                    <Badge variant="outline">{t('delivery.overview.metrics.notMeasured')}</Badge>
                  )}
                </td>
                <td className="py-1 text-xs text-foreground-muted">
                  {METRIC_BASIS[m.key] ?? publicDeliveryText(m.basis)}
                </td>
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
  const traceQuery = useDeliveryTraceability(overviewQuery.data?.projectId ?? null);

  if (overviewQuery.isLoading) {
    return <Skeleton className="h-96 w-full" />;
  }
  if (overviewQuery.isError || !overviewQuery.data) {
    return (
      <Card>
        <CardContent className="flex flex-col items-center gap-3 py-8 text-center text-sm text-error">
          <span>{t('delivery.error')}</span>
          <Button variant="outline" size="sm" onClick={() => void overviewQuery.refetch()}>
            {t('delivery.retry')}
          </Button>
        </CardContent>
      </Card>
    );
  }

  const o = overviewQuery.data;
  const e = o.executiveSummary;
  const trace = traceQuery.data;
  const agentNames = new Map((trace?.agents ?? []).map((agent) => [agent.id, agent.name]));
  const owner = e.owner ? (agentNames.get(e.owner) ?? e.owner) : null;
  const activePhase =
    trace?.phases.find((phase) => phase.state === 'active') ??
    [...(trace?.phases ?? [])].sort((a, b) => b.order - a.order).find((phase) => phase.state === 'completed') ??
    null;
  const relatedTasks = [...(trace?.tasks ?? [])].sort((a, b) => b.updatedAt.localeCompare(a.updatedAt));
  const evidenceAttempts = [...(trace?.attempts ?? [])]
    .filter((attempt) => attempt.summary || attempt.commitRefs.length > 0 || attempt.failureReason)
    .sort((a, b) => b.startedAt.localeCompare(a.startedAt));
  const nextSteps = [
    ...relatedTasks.filter((task) => task.state === 'blocked').map((task) => task.title),
    ...o.decisions.decisions.filter((decision) => !decision.resolved).map((decision) => decision.detail),
    ...o.documentation.checklist
      .filter((document) => !document.present)
      .map((document) => t('delivery.overview.trace.createDocument', { name: document.label })),
  ].slice(0, 8);

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
            <Stat label={t('delivery.overview.exec.owner')} value={owner ?? t('delivery.portfolio.noOwner')} />
            <Stat label={t('delivery.criticality.label')} value={t(`delivery.criticality.${e.criticality}`)} />
            <Stat label={t('delivery.overview.exec.milestones')} value={`${e.milestonesDone}/${e.milestonesTotal}`} />
            <Stat label={t('delivery.overview.exec.openTasks')} value={String(e.openTaskCount)} />
            <Stat label={t('delivery.overview.exec.blockedTasks')} value={String(e.blockedTaskCount)} />
            <Stat label={t('delivery.overview.exec.committed')} value={formatDate(e.committedDate, i18n.language) ?? t('delivery.portfolio.noDate')} />
            <Stat label={t('delivery.overview.exec.forecast')} value={formatDate(e.forecastDate, i18n.language) ?? t('delivery.portfolio.noDate')} />
            <Stat label={t('delivery.overview.exec.lastActivity')} value={formatDateTime(e.lastActivityAt, i18n.language) ?? '—'} />
          </dl>
          <div className="mt-4">
            <SourceDisclosure
              source={t('delivery.sources.overviewSource')}
              updatedAt={e.lastActivityAt}
              calculation={t('delivery.sources.overviewCalculation')}
              confidence={
                o.planAndMilestones.forecast.hasSufficientEvidence
                  ? t(`delivery.confidence.${o.planAndMilestones.forecast.confidence}`)
                  : t('delivery.sources.insufficient')
              }
              missing={[
                ...(owner ? [] : [t('delivery.sources.ownerMissing')]),
                ...(e.committedDate ? [] : [t('delivery.sources.dateMissing')]),
                ...(e.forecastDate ? [] : [t('delivery.sources.forecastMissing')]),
              ]}
              technical={o.risksAndDependencies.risks
                .map((risk) => `${risk.code}: ${risk.detail}`)
                .join(' · ')}
            />
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{t('delivery.overview.trace.contextTitle')}</CardTitle>
        </CardHeader>
        <CardContent>
          {traceQuery.isLoading ? (
            <Skeleton className="h-20 w-full" />
          ) : traceQuery.isError || !trace ? (
            <div className="flex flex-col items-start gap-2">
              <p className="text-sm text-foreground-muted">{t('delivery.capabilityUnavailable')}</p>
              <Button variant="outline" size="sm" onClick={() => void traceQuery.refetch()}>
                {t('delivery.retry')}
              </Button>
            </div>
          ) : (
            <dl className="grid gap-3 sm:grid-cols-3">
              <Stat label={t('delivery.overview.trace.objective')} value={trace.project.description || '—'} />
              <Stat label={t('delivery.overview.trace.phase')} value={activePhase?.name ?? t('delivery.overview.trace.noPhase')} />
              <Stat label={t('delivery.overview.trace.workflow')} value={trace.workflow ? t('delivery.overview.trace.linked') : t('delivery.overview.trace.notLinked')} />
            </dl>
          )}
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
            <SourceDisclosure
              source={t('delivery.sources.forecastSource')}
              updatedAt={o.planAndMilestones.forecast.createdAt ?? e.lastActivityAt}
              calculation={t('delivery.sources.forecastCalculation')}
              confidence={t(`delivery.confidence.${o.planAndMilestones.forecast.confidence}`)}
              missing={
                o.planAndMilestones.forecast.hasSufficientEvidence
                  ? []
                  : [t('delivery.sources.forecastEvidenceMissing')]
              }
              technical={o.planAndMilestones.forecast.basis
                .map((basis) => `${basis.signal}: ${basis.detail}`)
                .join(' · ')}
            />
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
                    <span className="text-foreground-muted">{attentionSignalLabel(r.code, t)}</span>
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
            {trace && trace.documents.length > 0 && (
              <div className="mt-4 border-t border-border pt-3">
                <p className="mb-2 text-xs font-medium text-foreground-muted">
                  {t('delivery.overview.docs.registered')}
                </p>
                <ul className="flex flex-col gap-2 text-sm">
                  {trace.documents.slice(0, 12).map((document) => (
                    <li key={document.id} className="flex items-start justify-between gap-3">
                      <span className="text-foreground">{document.title}</span>
                      <Badge variant="outline">
                        {t(`status.documentState.${document.state}`)}
                      </Badge>
                    </li>
                  ))}
                </ul>
              </div>
            )}
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

      <div className="grid gap-6 lg:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>{t('delivery.overview.trace.tasks')}</CardTitle>
          </CardHeader>
          <CardContent>
            {relatedTasks.length === 0 ? (
              <p className="text-sm text-foreground-muted">{t('delivery.overview.trace.noTasks')}</p>
            ) : (
              <ul className="flex flex-col gap-2 text-sm">
                {relatedTasks.slice(0, 12).map((task) => (
                  <li key={task.id} className="flex items-start justify-between gap-3">
                    <span className="text-foreground">{task.title}</span>
                    <Badge variant={task.state === 'blocked' ? 'warning' : 'outline'}>
                      {t(`status.taskState.${task.state}`)}
                    </Badge>
                  </li>
                ))}
              </ul>
            )}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>{t('delivery.overview.trace.evidence')}</CardTitle>
          </CardHeader>
          <CardContent>
            {evidenceAttempts.length === 0 ? (
              <p className="text-sm text-foreground-muted">{t('delivery.overview.trace.noEvidence')}</p>
            ) : (
              <ul className="flex flex-col gap-3 text-sm">
                {evidenceAttempts.slice(0, 10).map((attempt) => (
                  <li key={attempt.id}>
                    <p className="font-medium text-foreground">
                      {t('delivery.overview.trace.attempt', { number: attempt.number })}
                    </p>
                    <p className="text-foreground-muted">
                      {attempt.summary ?? attempt.failureReason ?? attempt.commitRefs.join(', ')}
                    </p>
                    {attempt.commitRefs.length > 0 && (
                      <p className="break-all font-mono text-xs text-foreground-muted">
                        {attempt.commitRefs.join(', ')}
                      </p>
                    )}
                  </li>
                ))}
              </ul>
            )}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>{t('delivery.overview.trace.activities')}</CardTitle>
          </CardHeader>
          <CardContent>
            {relatedTasks.length === 0 ? (
              <p className="text-sm text-foreground-muted">{t('delivery.overview.trace.noActivities')}</p>
            ) : (
              <ul className="flex flex-col gap-2 text-sm">
                {relatedTasks.slice(0, 8).map((task) => (
                  <li key={task.id} className="flex items-start justify-between gap-3">
                    <span className="text-foreground">{task.title}</span>
                    <time className="whitespace-nowrap text-xs text-foreground-muted" dateTime={task.updatedAt}>
                      {formatDateTime(task.updatedAt, i18n.language) ?? '—'}
                    </time>
                  </li>
                ))}
              </ul>
            )}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>{t('delivery.overview.trace.nextSteps')}</CardTitle>
          </CardHeader>
          <CardContent>
            {nextSteps.length === 0 ? (
              <p className="text-sm text-foreground-muted">{t('delivery.overview.trace.noNextSteps')}</p>
            ) : (
              <ul className="list-disc space-y-1 pl-5 text-sm text-foreground">
                {nextSteps.map((step, index) => (
                  <li key={`${step}-${index}`}>{step}</li>
                ))}
              </ul>
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
