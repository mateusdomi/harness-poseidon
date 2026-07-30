import { useTranslation } from 'react-i18next';

import { Badge, Card, CardContent, CardHeader, CardTitle } from '@/design-system';

import { useInsights } from '../../hooks/use-architecture-hub';
import { HubEmpty, HubError, HubLoading } from './hub-states';

function classificationVariant(value: string): 'error' | 'warning' | 'success' | 'info' | 'outline' {
  switch (value) {
    case 'eliminate':
      return 'error';
    case 'migrate':
      return 'warning';
    case 'tolerate':
      return 'info';
    case 'invest':
      return 'success';
    default:
      return 'outline';
  }
}

/**
 * ARC-07 — Insights & Racionalização. Relatório TIME (tolerate/invest/
 * migrate/eliminate) do portfólio, com racional, evidência e impacto de
 * dependentes por sistema.
 */
export function InsightsPanel({ projectId }: { projectId: string | null }) {
  const { t } = useTranslation();
  const query = useInsights(projectId);

  if (query.isLoading) return <HubLoading />;
  if (query.isError || !query.data) return <HubError onRetry={() => void query.refetch()} />;

  const report = query.data;

  return (
    <div className="flex flex-col gap-6" data-testid="insights-panel">
      <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-4">
        <Card>
          <CardContent className="flex flex-col gap-1 py-4">
            <span className="text-2xl font-semibold text-foreground">{report.systemCount}</span>
            <span className="text-xs text-foreground-muted">
              {t('architecture.hub.insights.systemCount')}
            </span>
          </CardContent>
        </Card>
        <Card>
          <CardContent className="flex flex-col gap-1 py-4">
            <span className="text-2xl font-semibold text-foreground">{report.insightCount}</span>
            <span className="text-xs text-foreground-muted">
              {t('architecture.hub.insights.insightCount')}
            </span>
          </CardContent>
        </Card>
        <Card className="md:col-span-2">
          <CardContent className="flex flex-wrap items-center gap-2 py-4">
            {Object.entries(report.byClassification).map(([classification, count]) => (
              <Badge key={classification} variant={classificationVariant(classification)}>
                {t(`architecture.hub.insights.classification.${classification}`, {
                  defaultValue: classification,
                })}
                {': '}
                {count}
              </Badge>
            ))}
          </CardContent>
        </Card>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>{t('architecture.hub.insights.title')}</CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col gap-3">
          {report.insights.length === 0 ? (
            <HubEmpty message={t('architecture.hub.insights.empty')} />
          ) : (
            report.insights.map((insight) => (
              <div
                key={insight.systemId}
                className="flex flex-col gap-1.5 border-b border-border py-2 last:border-0"
              >
                <div className="flex flex-wrap items-center gap-2">
                  <span className="font-medium text-foreground">{insight.systemName}</span>
                  <Badge variant={classificationVariant(insight.classification)}>
                    {t(`architecture.hub.insights.classification.${insight.classification}`, {
                      defaultValue: insight.classification,
                    })}
                  </Badge>
                  <Badge variant="outline">
                    {t(`architecture.hub.insights.category.${insight.category}`, {
                      defaultValue: insight.category,
                    })}
                  </Badge>
                  {insight.affectedDependentCount > 0 ? (
                    <Badge variant="warning">
                      {t('architecture.hub.insights.dependents', {
                        count: insight.affectedDependentCount,
                      })}
                    </Badge>
                  ) : null}
                </div>
                <p className="text-sm text-foreground">{insight.rationale}</p>
                <p className="text-xs text-foreground-muted">{insight.evidence}</p>
              </div>
            ))
          )}
        </CardContent>
      </Card>
    </div>
  );
}
