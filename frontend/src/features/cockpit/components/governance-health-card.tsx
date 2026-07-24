import { useQuery } from '@tanstack/react-query';
import { AlertTriangle, Info, ShieldCheck } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';

import { useApi } from '@/app/api-context';
import {
  Badge,
  Button,
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  Skeleton,
  Tooltip,
} from '@/design-system';

/** Resumo P1 do projeto; só é montado quando a flag operacional está ligada. */
export function GovernanceHealthCard({ projectId }: { projectId: string }) {
  const { t } = useTranslation();
  const api = useApi();
  const query = useQuery({
    queryKey: ['cockpit', 'governance-runtime', projectId],
    queryFn: () => api.listGovernanceReceipts({ projectId, limit: 25 }),
  });
  if (query.isLoading) return <Skeleton className="h-40 w-full lg:col-span-2" />;
  if (query.isError) {
    return (
      <Card className="border-warning lg:col-span-2">
        <CardContent className="flex items-center gap-3 p-5">
          <AlertTriangle aria-hidden="true" className="size-5 text-warning" />
          <p role="alert" className="flex-1 text-sm">{t('cockpitGovernance.error')}</p>
          <Button type="button" size="sm" variant="outline" onClick={() => void query.refetch()}>{t('common.actions.retry')}</Button>
        </CardContent>
      </Card>
    );
  }
  const receipts = query.data ?? [];
  const conflicts = receipts.reduce((sum, receipt) => sum + receipt.conflicts.length, 0);
  const truncated = receipts.reduce((sum, receipt) => sum + receipt.truncated.length, 0);
  const metrics = [
    { key: 'receipts' as const, value: receipts.length },
    { key: 'conflicts' as const, value: conflicts },
    { key: 'truncated' as const, value: truncated },
  ];
  return (
    <Card className={`lg:col-span-2 ${conflicts > 0 ? 'border-error' : ''}`}>
      <CardHeader className="flex-row flex-wrap items-center gap-2">
        <ShieldCheck aria-hidden="true" className="size-5 text-brand-strong" />
        <div className="flex flex-col">
          <CardTitle>{t('cockpitGovernance.title')}</CardTitle>
          <p className="text-xs text-foreground-muted">{t('cockpitGovernance.subtitle')}</p>
        </div>
        <Badge className="ml-auto" variant={conflicts > 0 ? 'error' : 'success'}>{conflicts > 0 ? t('cockpitGovernance.attention') : t('cockpitGovernance.healthy')}</Badge>
      </CardHeader>
      <CardContent className="flex flex-wrap items-end gap-4">
        <dl className="grid flex-1 grid-cols-3 gap-3 text-center">
          {metrics.map((metric) => (
            <div key={metric.key}>
              <dt className="flex items-center justify-center gap-1 text-xs text-foreground-muted">
                {t(`cockpitGovernance.${metric.key}`)}
                <Tooltip label={t(`cockpitGovernance.tooltips.${metric.key}`)}>
                  <button
                    type="button"
                    aria-label={t(`cockpitGovernance.tooltips.${metric.key}`)}
                    className="rounded-full text-foreground-muted hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                  >
                    <Info aria-hidden="true" className="size-3.5" />
                  </button>
                </Tooltip>
              </dt>
              <dd className="font-heading text-2xl font-semibold">{metric.value}</dd>
            </div>
          ))}
        </dl>
        <Button asChild size="sm" variant="outline"><Link to="/governance">{t('cockpitGovernance.open')}</Link></Button>
      </CardContent>
    </Card>
  );
}
