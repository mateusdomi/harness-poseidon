import { useTranslation } from 'react-i18next';

import { Badge, Card, CardContent, CardHeader, CardTitle } from '@/design-system';

import { useDiscoveries, useDiscoverySummary } from '../../hooks/use-architecture-hub';
import { HubEmpty, HubError, HubLoading } from './hub-states';

function statusVariant(status: string): 'success' | 'warning' | 'outline' {
  if (status === 'confirmed') return 'success';
  if (status === 'open') return 'warning';
  return 'outline';
}

function confidenceVariant(value: string): 'success' | 'warning' | 'error' | 'outline' {
  if (value === 'high') return 'success';
  if (value === 'medium') return 'warning';
  if (value === 'low') return 'error';
  return 'outline';
}

/**
 * ARC-06 — Discovery. Descobertas de arquitetura por sistema, sua confiança,
 * evidência e perguntas pendentes, com um resumo agregado por assunto.
 */
export function DiscoveryPanel({ projectId }: { projectId: string | null }) {
  const { t } = useTranslation();
  const discoveries = useDiscoveries(projectId);
  const summary = useDiscoverySummary(projectId);

  if (discoveries.isLoading || summary.isLoading) return <HubLoading />;
  if (discoveries.isError) return <HubError onRetry={() => void discoveries.refetch()} />;

  const items = discoveries.data?.discoveries ?? [];

  return (
    <div className="flex flex-col gap-6">
      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3" data-testid="discovery-summary">
        {(summary.data?.subjects ?? []).map((subject) => (
          <Card key={subject.subjectName}>
            <CardHeader>
              <CardTitle>{subject.subjectName}</CardTitle>
            </CardHeader>
            <CardContent className="flex flex-col gap-2 text-sm">
              <div className="flex flex-wrap gap-1.5">
                <Badge variant={confidenceVariant(subject.overallConfidence)}>
                  {t(`architecture.hub.confidence.${subject.overallConfidence}`, {
                    defaultValue: subject.overallConfidence,
                  })}
                </Badge>
                <Badge variant="outline">
                  {t('architecture.hub.discovery.counts', {
                    open: subject.openCount,
                    confirmed: subject.confirmedCount,
                  })}
                </Badge>
              </div>
              {subject.pendingQuestions.length > 0 ? (
                <div>
                  <p className="text-xs font-medium text-foreground-muted">
                    {t('architecture.hub.discovery.pending')}
                  </p>
                  <ul className="list-disc pl-5 text-xs text-foreground-muted">
                    {subject.pendingQuestions.map((q) => (
                      <li key={q}>{q}</li>
                    ))}
                  </ul>
                </div>
              ) : null}
            </CardContent>
          </Card>
        ))}
      </div>

      <Card>
        <CardHeader>
          <CardTitle>{t('architecture.hub.discovery.title')}</CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col gap-3">
          {items.length === 0 ? (
            <HubEmpty message={t('architecture.hub.discovery.empty')} />
          ) : (
            items.map((d) => (
              <div key={d.id} className="flex flex-col gap-1.5 border-b border-border py-2 last:border-0">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="font-medium text-foreground">{d.subjectName}</span>
                  <Badge variant="outline">{d.field}</Badge>
                  <Badge variant={statusVariant(d.status)}>
                    {t(`architecture.hub.discovery.status.${d.status}`, { defaultValue: d.status })}
                  </Badge>
                  <Badge variant={confidenceVariant(d.confidence)}>
                    {t(`architecture.hub.confidence.${d.confidence}`, { defaultValue: d.confidence })}
                  </Badge>
                  <Badge variant="info">
                    {t(`architecture.hub.discovery.source.${d.sourceKind}`, {
                      defaultValue: d.sourceKind,
                    })}
                  </Badge>
                </div>
                <p className="text-sm text-foreground">{d.value}</p>
                <p className="text-xs text-foreground-muted">{d.evidence}</p>
              </div>
            ))
          )}
        </CardContent>
      </Card>
    </div>
  );
}
