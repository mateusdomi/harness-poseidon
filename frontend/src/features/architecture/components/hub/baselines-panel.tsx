import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { Badge, Button, Card, CardContent, CardHeader, CardTitle } from '@/design-system';

import { useBaselineComparison, useBaselines } from '../../hooks/use-architecture-hub';
import { HubEmpty, HubError, HubLoading } from './hub-states';

function driftVariant(drift: string): 'success' | 'warning' | 'error' | 'outline' {
  switch (drift) {
    case 'matched':
      return 'success';
    case 'missing':
      return 'error';
    case 'unplanned':
      return 'warning';
    default:
      return 'outline';
  }
}

function conformanceVariant(percent: number): 'success' | 'warning' | 'error' {
  if (percent >= 85) return 'success';
  if (percent >= 60) return 'warning';
  return 'error';
}

function ComparisonView({ baselineId }: { baselineId: string }) {
  const { t } = useTranslation();
  const query = useBaselineComparison(baselineId);
  if (query.isLoading) return <HubLoading />;
  if (query.isError || !query.data) return <HubError onRetry={() => void query.refetch()} />;
  const c = query.data;
  const percent = Math.round(c.conformancePercent * 10) / 10;

  return (
    <div className="flex flex-col gap-4" data-testid="baseline-comparison">
      <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-4">
        <Card>
          <CardContent className="flex flex-col gap-1 py-4">
            <Badge variant={conformanceVariant(percent)}>
              {t('architecture.hub.baselines.conformance', { percent })}
            </Badge>
            <span className="text-xs text-foreground-muted">
              {t('architecture.hub.baselines.conformanceLabel')}
            </span>
          </CardContent>
        </Card>
        <Card>
          <CardContent className="flex flex-col gap-1 py-4">
            <span className="text-lg font-semibold text-foreground">
              {t('architecture.hub.baselines.nodeStats', {
                matched: c.matched,
                missing: c.missing,
                unplanned: c.unplanned,
              })}
            </span>
            <span className="text-xs text-foreground-muted">
              {t('architecture.hub.baselines.nodes')}
            </span>
          </CardContent>
        </Card>
        <Card>
          <CardContent className="flex flex-col gap-1 py-4">
            <span className="text-lg font-semibold text-foreground">
              {t('architecture.hub.baselines.nodeStats', {
                matched: c.edgeMatched,
                missing: c.edgeMissing,
                unplanned: c.edgeUnplanned,
              })}
            </span>
            <span className="text-xs text-foreground-muted">
              {t('architecture.hub.baselines.edges')}
            </span>
          </CardContent>
        </Card>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>{t('architecture.hub.baselines.drifts')}</CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col gap-2">
          {c.drifts.length === 0 ? (
            <HubEmpty message={t('architecture.hub.baselines.noDrift')} />
          ) : (
            c.drifts.map((drift) => (
              <div key={drift.elementId} className="flex flex-wrap items-center gap-2 border-b border-border py-2 text-sm last:border-0">
                <span className="font-medium text-foreground">{drift.name}</span>
                <Badge variant="outline">{drift.kind}</Badge>
                <Badge variant={driftVariant(drift.drift)}>
                  {t(`architecture.hub.baselines.drift.${drift.drift}`, { defaultValue: drift.drift })}
                </Badge>
              </div>
            ))
          )}
        </CardContent>
      </Card>
    </div>
  );
}

/**
 * ARC-10 — Baselines & Conformidade. Baselines da entrega com comparação
 * planejado × as-built (conformidade, drifts de nós e arestas).
 */
export function BaselinesPanel({ projectId }: { projectId: string | null }) {
  const { t } = useTranslation();
  const [openId, setOpenId] = useState<string | null>(null);
  const query = useBaselines(projectId);

  if (query.isLoading) return <HubLoading />;
  if (query.isError || !query.data) return <HubError onRetry={() => void query.refetch()} />;

  const baselines = query.data.baselines;

  if (openId) {
    const baseline = baselines.find((b) => b.id === openId);
    return (
      <div className="flex flex-col gap-4">
        <div className="flex items-center gap-3">
          <Button type="button" variant="outline" size="sm" onClick={() => setOpenId(null)}>
            {t('common.actions.back')}
          </Button>
          <h2 className="text-lg font-semibold text-foreground">
            {baseline?.title ?? t('architecture.hub.baselines.title')}
          </h2>
        </div>
        <ComparisonView baselineId={openId} />
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-3" data-testid="baselines-list">
      {baselines.length === 0 ? (
        <HubEmpty message={t('architecture.hub.baselines.empty')} />
      ) : (
        baselines.map((baseline) => (
          <Card key={baseline.id}>
            <CardContent className="flex flex-wrap items-center justify-between gap-3 py-4">
              <div className="flex flex-col gap-1">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="font-medium text-foreground">{baseline.title}</span>
                  <Badge variant={baseline.status === 'closed' ? 'success' : 'warning'}>
                    {t(`architecture.hub.baselines.status.${baseline.status}`, {
                      defaultValue: baseline.status,
                    })}
                  </Badge>
                  {baseline.hasAsBuilt ? (
                    <Badge variant="info">{t('architecture.hub.baselines.asBuilt')}</Badge>
                  ) : null}
                </div>
                <span className="text-xs text-foreground-muted">
                  {t('architecture.hub.baselines.elementCount', {
                    count: baseline.baselineElementCount,
                  })}
                </span>
              </div>
              <Button type="button" variant="outline" size="sm" onClick={() => setOpenId(baseline.id)}>
                {t('architecture.hub.baselines.compare')}
              </Button>
            </CardContent>
          </Card>
        ))
      )}
    </div>
  );
}
