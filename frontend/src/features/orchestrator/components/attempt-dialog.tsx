import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';

import { attemptEventSchema, type Attempt } from '@/api';
import { Badge, Button, Input, Select, Skeleton } from '@/design-system';
import { formatDateTime } from '@/lib/format';
import { maskSecrets } from '@/lib/secrets';
import { ModalDialog } from '@/features/shared/components/modal-dialog';
import {
  filterAttemptEvents,
  sortAttemptEvents,
  type AttemptEventKindFilter,
} from '@/features/orchestrator/lib/orchestrator-derive';
import { useAttemptEvents } from '@/features/orchestrator/hooks/use-orchestrator';

export interface AttemptDialogProps {
  attempt: Attempt;
  onClose: () => void;
}

/**
 * Detalhe da tentativa ("Abrir"): linha do tempo interpretada (eventos em
 * ordem cronológica, kind → label do i18n) + log estruturado com filtro por
 * tipo e busca textual. Conteúdo sempre passa por `maskSecrets` — a UI
 * NUNCA exibe segredos (nota `common.secrets.maskedNote` visível).
 */
export function AttemptDialog({ attempt, onClose }: AttemptDialogProps) {
  const { t } = useTranslation();
  const eventsQuery = useAttemptEvents(attempt.id);
  const [kindFilter, setKindFilter] = useState<AttemptEventKindFilter>('');
  const [search, setSearch] = useState('');

  const timeline = useMemo(
    () => sortAttemptEvents(eventsQuery.data ?? []),
    [eventsQuery.data],
  );
  const logEntries = useMemo(
    () => filterAttemptEvents(timeline, kindFilter, search),
    [timeline, kindFilter, search],
  );

  return (
    <ModalDialog
      label={t('orchestrator.attempt.title', { number: attempt.number })}
      onClose={onClose}
      className="max-w-2xl"
    >
      <h2 className="font-heading text-lg font-semibold">
        {t('orchestrator.attempt.title', { number: attempt.number })}
      </h2>

      {eventsQuery.isPending ? (
        <div className="flex flex-col gap-2" role="status" aria-label={t('common.states.loading')}>
          <Skeleton className="h-8 w-full" />
          <Skeleton className="h-24 w-full" />
          <Skeleton className="h-24 w-full" />
        </div>
      ) : eventsQuery.isError ? (
        <div className="flex flex-col items-start gap-3">
          <p role="alert" className="text-sm text-error">
            {t('common.states.errorBody')}
          </p>
          <Button type="button" variant="outline" onClick={() => void eventsQuery.refetch()}>
            {t('common.actions.retry')}
          </Button>
        </div>
      ) : timeline.length === 0 ? (
        <p className="text-sm text-foreground-muted">{t('orchestrator.attempt.empty')}</p>
      ) : (
        <>
          <section aria-label={t('orchestrator.attempt.timeline')}>
            <h3 className="mb-2 text-sm font-semibold">{t('orchestrator.attempt.timeline')}</h3>
            <ol className="flex flex-col gap-2 border-l border-border pl-3">
              {timeline.map((event) => (
                <li key={event.id} className="flex flex-col gap-0.5">
                  <span className="flex flex-wrap items-center gap-2 text-xs text-foreground-muted">
                    <Badge variant="outline">{t(`status.attemptEventKind.${event.kind}`)}</Badge>
                    {formatDateTime(event.occurredAt)}
                  </span>
                  <p className="text-sm">{maskSecrets(event.content)}</p>
                </li>
              ))}
            </ol>
          </section>

          <section aria-label={t('orchestrator.attempt.log')} className="flex flex-col gap-2">
            <h3 className="text-sm font-semibold">{t('orchestrator.attempt.log')}</h3>
            <div className="flex flex-wrap items-end gap-3">
              <div className="flex flex-col gap-1">
                <label htmlFor="attempt-filter-kind" className="text-xs font-medium">
                  {t('orchestrator.attempt.filterKind')}
                </label>
                <Select
                  id="attempt-filter-kind"
                  value={kindFilter}
                  onChange={(event) =>
                    setKindFilter(event.target.value as AttemptEventKindFilter)
                  }
                >
                  <option value="">{t('orchestrator.attempt.allKinds')}</option>
                  {attemptEventSchema.shape.kind.options.map((kind) => (
                    <option key={kind} value={kind}>
                      {t(`status.attemptEventKind.${kind}`)}
                    </option>
                  ))}
                </Select>
              </div>
              <div className="flex min-w-40 flex-1 flex-col gap-1">
                <label htmlFor="attempt-search" className="text-xs font-medium">
                  {t('orchestrator.attempt.searchLabel')}
                </label>
                <Input
                  id="attempt-search"
                  value={search}
                  placeholder={t('orchestrator.attempt.searchPlaceholder')}
                  onChange={(event) => setSearch(event.target.value)}
                />
              </div>
            </div>
            <p className="text-xs text-foreground-muted">{t('common.secrets.maskedNote')}</p>
            {logEntries.length === 0 ? (
              <p className="text-sm text-foreground-muted">
                {t('orchestrator.attempt.noResults')}
              </p>
            ) : (
              <ul className="flex max-h-48 flex-col gap-1 overflow-y-auto rounded-md border border-border bg-surface-elevated p-2">
                {logEntries.map((event) => (
                  <li key={event.id} className="flex items-start gap-2 text-xs">
                    <Badge variant="outline">{t(`status.attemptEventKind.${event.kind}`)}</Badge>
                    <span className="break-all">{maskSecrets(event.content)}</span>
                  </li>
                ))}
              </ul>
            )}
          </section>
        </>
      )}
    </ModalDialog>
  );
}
