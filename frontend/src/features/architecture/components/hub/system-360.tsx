import { useTranslation } from 'react-i18next';

import { Badge, Button, Card, CardContent, CardHeader, CardTitle } from '@/design-system';

import { useSystem360 } from '../../hooks/use-architecture-hub';
import { HubError, HubLoading } from './hub-states';

function Facet({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>{title}</CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-2 text-sm">{children}</CardContent>
    </Card>
  );
}

function Row({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div className="flex items-start justify-between gap-3 border-b border-border py-1.5 last:border-0">
      <span className="text-foreground-muted">{label}</span>
      <span className="text-right font-medium text-foreground">{value}</span>
    </div>
  );
}

/**
 * ARC-03 — Sistema 360. Ficha completa de um sistema em seis facetas:
 * negócio, tecnologia, integrações, operação, dados e governança.
 */
export function System360Panel({ systemId, onBack }: {
  systemId: string;
  onBack: () => void;
}) {
  const { t } = useTranslation();
  const query = useSystem360(systemId);

  if (query.isLoading) return <HubLoading />;
  if (query.isError || !query.data) return <HubError onRetry={() => void query.refetch()} />;

  const data = query.data;
  const dash = t('architecture.hub.system360.notSet');

  return (
    <div className="flex flex-col gap-4" data-testid="system-360">
      <div className="flex items-center gap-3">
        <Button type="button" variant="outline" size="sm" onClick={onBack}>
          {t('common.actions.back')}
        </Button>
        <div>
          <h2 className="text-xl font-semibold text-foreground">{data.business.name}</h2>
          <p className="text-sm text-foreground-muted">{data.business.description}</p>
        </div>
      </div>

      <div className="grid gap-4 lg:grid-cols-2">
        <Facet title={t('architecture.hub.system360.business')}>
          <Row
            label={t('architecture.hub.system360.criticality')}
            value={t(`architecture.hub.criticality.${data.business.criticality}`, {
              defaultValue: data.business.criticality,
            })}
          />
          <Row label={t('architecture.hub.system360.domain')} value={data.business.domain ?? dash} />
          <Row label={t('architecture.hub.system360.owner')} value={data.business.owner ?? dash} />
          <div className="flex flex-wrap gap-1.5 pt-1">
            {data.business.capabilities.map((cap) => (
              <Badge key={cap} variant="outline">
                {cap}
              </Badge>
            ))}
          </div>
        </Facet>

        <Facet title={t('architecture.hub.system360.technology')}>
          <Row
            label={t('architecture.hub.system360.lifecycle')}
            value={
              data.technology.lifecycleStatus
                ? t(`architecture.hub.lifecycle.${data.technology.lifecycleStatus}`, {
                    defaultValue: data.technology.lifecycleStatus,
                  })
                : dash
            }
          />
          <div className="flex flex-wrap gap-1.5 pt-1">
            {data.technology.techStack.map((tech) => (
              <Badge key={tech} variant="info">
                {tech}
              </Badge>
            ))}
          </div>
          <Row
            label={t('architecture.hub.system360.containers')}
            value={data.technology.containers.length}
          />
        </Facet>

        <Facet title={t('architecture.hub.system360.integrations')}>
          <p className="text-xs font-medium uppercase text-foreground-muted">
            {t('architecture.hub.system360.outgoing')}
          </p>
          {data.integrations.outgoing.length === 0 ? (
            <span className="text-xs text-foreground-muted">{dash}</span>
          ) : (
            data.integrations.outgoing.map((edge) => (
              <div key={`out-${edge.targetId}-${edge.kind}`} className="flex items-center gap-2 text-xs">
                <Badge variant="outline">{edge.kind}</Badge>
                <span className="text-foreground">{edge.targetName}</span>
              </div>
            ))
          )}
          <p className="pt-2 text-xs font-medium uppercase text-foreground-muted">
            {t('architecture.hub.system360.incoming')}
          </p>
          {data.integrations.incoming.length === 0 ? (
            <span className="text-xs text-foreground-muted">{dash}</span>
          ) : (
            data.integrations.incoming.map((edge) => (
              <div key={`in-${edge.sourceId}-${edge.kind}`} className="flex items-center gap-2 text-xs">
                <span className="text-foreground">{edge.sourceName}</span>
                <Badge variant="outline">{edge.kind}</Badge>
              </div>
            ))
          )}
        </Facet>

        <Facet title={t('architecture.hub.system360.operation')}>
          <Row label={t('architecture.hub.system360.sla')} value={data.operation.sla ?? dash} />
          <Row
            label={t('architecture.hub.system360.incidents')}
            value={data.operation.incidentCount}
          />
          <Row
            label={t('architecture.hub.system360.cost')}
            value={
              data.operation.costMonthlyUsd !== null
                ? t('architecture.hub.system360.costValue', { value: data.operation.costMonthlyUsd })
                : dash
            }
          />
          <Row label={t('architecture.hub.system360.dr')} value={data.operation.drPolicy ?? dash} />
        </Facet>

        <Facet title={t('architecture.hub.system360.data')}>
          <Row
            label={t('architecture.hub.system360.pii')}
            value={
              data.data.pii
                ? t('architecture.hub.system360.yes')
                : t('architecture.hub.system360.no')
            }
          />
          <Row
            label={t('architecture.hub.system360.sensitive')}
            value={
              data.data.sensitive
                ? t('architecture.hub.system360.yes')
                : t('architecture.hub.system360.no')
            }
          />
          <Row label={t('architecture.hub.system360.retention')} value={data.data.retention ?? dash} />
          <div className="flex flex-wrap gap-1.5 pt-1">
            {data.data.dataClasses.map((cls) => (
              <Badge key={cls} variant="outline">
                {cls}
              </Badge>
            ))}
          </div>
        </Facet>

        <Facet title={t('architecture.hub.system360.governance')}>
          <Row label={t('architecture.hub.system360.adrCount')} value={data.governance.adrCount} />
          <Row
            label={t('architecture.hub.system360.busFactor')}
            value={data.governance.busFactor ?? dash}
          />
          <Row
            label={t('architecture.hub.system360.reviewConfidence')}
            value={
              data.governance.reviewConfidence
                ? t(`architecture.hub.confidence.${data.governance.reviewConfidence}`, {
                    defaultValue: data.governance.reviewConfidence,
                  })
                : dash
            }
          />
          {data.governance.risks.length > 0 ? (
            <ul className="list-disc pl-5 text-xs text-foreground-muted">
              {data.governance.risks.map((risk) => (
                <li key={risk}>{risk}</li>
              ))}
            </ul>
          ) : null}
        </Facet>
      </div>
    </div>
  );
}
