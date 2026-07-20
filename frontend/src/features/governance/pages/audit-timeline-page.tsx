import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Download, ScrollText, ShieldCheck } from 'lucide-react';

import { Button, Card, CardContent, Skeleton } from '@/design-system';
import { AuditEventItem } from '@/features/governance/components/audit-event-item';
import { AuditFiltersBar } from '@/features/governance/components/audit-filters-bar';
import {
  useGovernanceData,
  useGovernanceRealtime,
} from '@/features/governance/hooks/use-governance';
import {
  EMPTY_AUDIT_FILTERS,
  filterAuditEvents,
  type AuditFilters,
} from '@/features/governance/lib/audit-derive';
import {
  AUDIT_EXPORT_FILENAMES,
  auditEventsToCsv,
  auditEventsToJson,
  downloadTextFile,
} from '@/features/governance/lib/audit-export';
import { useNow } from '@/features/shared/hooks/use-now';
import { PaginationBar } from '@/features/shared/components/pagination';
import { usePagination } from '@/features/shared/hooks/use-pagination';

/**
 * Governança e auditoria: timeline pesquisável da trilha de auditoria com
 * filtros correlacionados (ator, projeto, tarefa, tentativa, modelo,
 * ferramenta e período), expansão com detalhe mascarado + correlação da
 * entidade alvo e exportação client-side (JSON/CSV) dos eventos filtrados.
 * Tempo real via `audit.eventAppended` no stream global.
 */
export default function AuditTimelinePage() {
  const { t } = useTranslation();
  const { events, catalog, isPending, isError, refetch } = useGovernanceData();
  useGovernanceRealtime();
  const now = useNow();

  const [filters, setFilters] = useState<AuditFilters>(EMPTY_AUDIT_FILTERS);
  const patchFilters = (patch: Partial<AuditFilters>) =>
    setFilters((current) => ({ ...current, ...patch }));

  const timeline = useMemo(
    () => filterAuditEvents(events, filters, catalog),
    [events, filters, catalog],
  );

  // Paginação client-side sobre a timeline filtrada (a exportação JSON/CSV
  // continua usando a lista COMPLETA). Reset ao mudar filtros.
  const pagination = usePagination(timeline.length, { resetKey: filters });

  const exportJson = () =>
    downloadTextFile(AUDIT_EXPORT_FILENAMES.json, auditEventsToJson(timeline), 'application/json');
  const exportCsv = () =>
    downloadTextFile(AUDIT_EXPORT_FILENAMES.csv, auditEventsToCsv(timeline), 'text/csv');

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="font-heading text-2xl font-semibold">{t('governance.title')}</h1>
        {!isPending && !isError && (
          <span className="text-sm text-foreground-muted">
            {t('governance.timeline.count', { count: timeline.length })}
          </span>
        )}
        {!isPending && !isError && (
          <div className="ms-auto flex flex-wrap gap-2">
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={timeline.length === 0}
              onClick={exportJson}
            >
              <Download aria-hidden="true" />
              {t('governance.export.json')}
            </Button>
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={timeline.length === 0}
              onClick={exportCsv}
            >
              <Download aria-hidden="true" />
              {t('governance.export.csv')}
            </Button>
          </div>
        )}
      </div>

      <p className="flex items-center gap-2 text-xs text-foreground-muted">
        <ShieldCheck aria-hidden="true" className="size-4" />
        {t('common.secrets.maskedNote')}
      </p>

      {isPending ? (
        <div className="flex flex-col gap-3" role="status" aria-label={t('common.states.loading')}>
          <Skeleton className="h-10 w-full" />
          {Array.from({ length: 4 }, (_, index) => (
            <Skeleton key={index} className="h-28 w-full" />
          ))}
        </div>
      ) : isError ? (
        <div className="flex flex-col items-start gap-3">
          <p role="alert" className="text-sm text-error">
            {t('common.states.errorBody')}
          </p>
          <Button type="button" variant="outline" onClick={refetch}>
            {t('common.actions.retry')}
          </Button>
        </div>
      ) : (
        <>
          <AuditFiltersBar filters={filters} onChange={patchFilters} catalog={catalog} />

          {timeline.length === 0 ? (
            <Card>
              <CardContent className="flex flex-col items-center gap-3 p-6 text-center">
                <ScrollText aria-hidden="true" className="size-8 text-foreground-muted" />
                <h2 className="font-heading text-lg font-semibold">
                  {t('governance.empty.title')}
                </h2>
                <p className="text-sm text-foreground-muted">{t('governance.empty.body')}</p>
              </CardContent>
            </Card>
          ) : (
            <>
              <ul className="flex flex-col gap-3" aria-label={t('governance.timeline.label')}>
                {pagination.paginate(timeline).map((event) => (
                  <AuditEventItem key={event.id} event={event} catalog={catalog} now={now} />
                ))}
              </ul>
              <PaginationBar pagination={pagination} />
            </>
          )}
        </>
      )}
    </div>
  );
}
