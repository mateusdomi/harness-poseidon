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
  // Tom por métrica: comprovantes é neutro/marca; conflitos e cortes acendem
  // (erro/aviso) só quando há ocorrência — o estado é lido pela forma, não só
  // pelo número.
  const metrics = [
    { key: 'receipts' as const, value: receipts.length, tone: 'brand' as const },
    { key: 'conflicts' as const, value: conflicts, tone: conflicts > 0 ? ('error' as const) : ('muted' as const) },
    { key: 'truncated' as const, value: truncated, tone: truncated > 0 ? ('warning' as const) : ('muted' as const) },
  ];
  const toneClass: Record<'brand' | 'error' | 'warning' | 'muted', string> = {
    brand: 'text-gradient-brand',
    error: 'text-error',
    warning: 'text-warning',
    muted: 'text-foreground',
  };
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
      <CardContent className="flex flex-col gap-4">
        {/* Cada métrica é um tile isolado e delimitado: o rótulo quebra dentro da
            própria caixa (nada de colisão/sobreposição entre colunas no mobile a
            375px, que era a quebra reportada). 3 colunas em qualquer largura, mas
            cada uma contida. */}
        <dl className="grid grid-cols-3 gap-2">
          {metrics.map((metric) => (
            <div
              key={metric.key}
              className="flex min-w-0 flex-col items-center gap-1 rounded-md border border-border bg-surface-elevated p-3 text-center"
            >
              <dd
                className={`font-heading text-2xl font-bold leading-none tabular-nums ${toneClass[metric.tone]}`}
              >
                {metric.value}
              </dd>
              <dt className="flex items-center justify-center gap-1 text-[11px] leading-tight text-foreground-muted">
                <span className="min-w-0 text-balance">{t(`cockpitGovernance.${metric.key}`)}</span>
                <Tooltip label={t(`cockpitGovernance.tooltips.${metric.key}`)}>
                  <button
                    type="button"
                    aria-label={t(`cockpitGovernance.tooltips.${metric.key}`)}
                    className="shrink-0 rounded-full text-foreground-muted hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                  >
                    <Info aria-hidden="true" className="size-3.5" />
                  </button>
                </Tooltip>
              </dt>
            </div>
          ))}
        </dl>
        <Button asChild size="sm" variant="outline" className="w-full sm:w-auto sm:self-start">
          <Link to="/governance">{t('cockpitGovernance.open')}</Link>
        </Button>
      </CardContent>
    </Card>
  );
}
