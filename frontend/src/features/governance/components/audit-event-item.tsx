import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronDown, ChevronUp } from 'lucide-react';

import type { AuditEvent } from '@/api';
import { Badge, Button } from '@/design-system';
import {
  correlateAuditEvent,
  resolveActorName,
  resolveTargetName,
  type AuditCatalog,
} from '@/features/governance/lib/audit-derive';
import {
  formatCurrencyUSD,
  formatDateTime,
  formatDurationMs,
  formatNumber,
  formatRelativeTime,
} from '@/lib/format';
import { maskSecrets } from '@/lib/secrets';
import {
  approvalStateVariant,
  attemptStateVariant,
  auditActorKindVariant,
  taskStateVariant,
} from '@/lib/status';

interface AuditEventItemProps {
  event: AuditEvent;
  catalog: AuditCatalog;
  now: Date;
}

/** Rótulo/valor do bloco de correlação (attempt, aprovação, tarefa). */
function CorrelationField({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-0.5">
      <dt className="text-xs text-foreground-muted">{label}</dt>
      <dd className="text-sm">{children}</dd>
    </div>
  );
}

/**
 * Item da timeline de auditoria: badge do tipo de ator, ator resolvido,
 * ação (mono), alvo (tipo + nome resolvido) e data absoluta + relativa.
 * Expansão (botão ≥44px): detalhe mascarado + correlação com a entidade
 * alvo (attempt → custo/tokens/duração/estado; approval → estado/resolução;
 * task → título/estado).
 */
export function AuditEventItem({ event, catalog, now }: AuditEventItemProps) {
  const { t } = useTranslation();
  const [expanded, setExpanded] = useState(false);

  const actorName = resolveActorName(event, catalog);
  const targetName = resolveTargetName(event, catalog);
  const correlation = expanded ? correlateAuditEvent(event, catalog) : null;
  const { attempt, approval, task } = correlation ?? { attempt: null, approval: null, task: null };

  return (
    <li className="flex flex-col rounded-lg border border-border bg-surface text-foreground shadow-sm">
      <div className="flex flex-col gap-2 p-4">
        <div className="flex flex-wrap items-center gap-2">
          <Badge variant={auditActorKindVariant(event.actorKind)}>
            {t(`status.auditActorKind.${event.actorKind}`)}
          </Badge>
          {actorName !== null && <span className="text-sm font-medium">{actorName}</span>}
          <code className="rounded bg-surface-elevated px-1.5 py-0.5 font-mono text-xs">
            {event.action}
          </code>
        </div>

        <div className="flex flex-wrap items-center gap-2 text-sm text-foreground-muted">
          <code className="font-mono text-xs">{event.targetType}</code>
          {targetName !== null ? (
            <span className="text-foreground">{targetName}</span>
          ) : (
            <span>{t('governance.event.unknownTarget')}</span>
          )}
        </div>

        <div className="flex flex-wrap items-center justify-between gap-2">
          <span className="text-xs text-foreground-muted">
            <time dateTime={event.occurredAt}>{formatDateTime(event.occurredAt)}</time>
            {' · '}
            {formatRelativeTime(event.occurredAt, undefined, now)}
          </span>
          <Button
            type="button"
            variant="outline"
            size="sm"
            aria-expanded={expanded}
            onClick={() => setExpanded((current) => !current)}
          >
            {expanded ? (
              <ChevronUp aria-hidden="true" />
            ) : (
              <ChevronDown aria-hidden="true" />
            )}
            {expanded ? t('governance.event.collapse') : t('governance.event.expand')}
          </Button>
        </div>

        {expanded && (
          <div className="flex flex-col gap-3 border-t border-border pt-3">
            <div className="flex flex-col gap-1">
              <h3 className="text-xs font-medium text-foreground-muted">
                {t('governance.event.detail')}
              </h3>
              {event.detail !== null ? (
                <p className="whitespace-pre-wrap text-sm">{maskSecrets(event.detail)}</p>
              ) : (
                <p className="text-sm text-foreground-muted">{t('governance.event.noDetail')}</p>
              )}
            </div>

            {(attempt !== null || approval !== null || task !== null) && (
              <div className="flex flex-col gap-2">
                <h3 className="text-xs font-medium text-foreground-muted">
                  {t('governance.correlation.title')}
                </h3>
                <dl className="flex flex-wrap gap-4">
                  {attempt !== null && (
                    <>
                      <CorrelationField label={t('governance.correlation.attempt')}>
                        {t('governance.correlation.attemptValue', { number: attempt.number })}
                      </CorrelationField>
                      <CorrelationField label={t('governance.correlation.state')}>
                        <Badge variant={attemptStateVariant(attempt.state)}>
                          {t(`status.attemptState.${attempt.state}`)}
                        </Badge>
                      </CorrelationField>
                      <CorrelationField label={t('governance.correlation.cost')}>
                        {formatCurrencyUSD(attempt.costUsd)}
                      </CorrelationField>
                      <CorrelationField label={t('governance.correlation.tokens')}>
                        {formatNumber(attempt.tokensInput)}
                        {' / '}
                        {formatNumber(attempt.tokensOutput)}
                      </CorrelationField>
                      {attempt.durationMs !== null && (
                        <CorrelationField label={t('governance.correlation.duration')}>
                          {formatDurationMs(attempt.durationMs)}
                        </CorrelationField>
                      )}
                    </>
                  )}
                  {approval !== null && (
                    <>
                      <CorrelationField label={t('governance.correlation.approval')}>
                        {approval.title}
                      </CorrelationField>
                      <CorrelationField label={t('governance.correlation.state')}>
                        <Badge variant={approvalStateVariant(approval.state)}>
                          {t(`status.approvalState.${approval.state}`)}
                        </Badge>
                      </CorrelationField>
                      {approval.resolutionNote !== null && (
                        <CorrelationField label={t('governance.correlation.resolution')}>
                          {maskSecrets(approval.resolutionNote)}
                        </CorrelationField>
                      )}
                      {approval.resolvedAt !== null && (
                        <CorrelationField label={t('governance.correlation.resolvedAt')}>
                          <time dateTime={approval.resolvedAt}>
                            {formatDateTime(approval.resolvedAt)}
                          </time>
                        </CorrelationField>
                      )}
                    </>
                  )}
                  {task !== null && (
                    <>
                      <CorrelationField label={t('governance.correlation.task')}>
                        {task.title}
                      </CorrelationField>
                      <CorrelationField label={t('governance.correlation.state')}>
                        <Badge variant={taskStateVariant(task.state)}>
                          {t(`status.taskState.${task.state}`)}
                        </Badge>
                      </CorrelationField>
                    </>
                  )}
                </dl>
              </div>
            )}
          </div>
        )}
      </div>
    </li>
  );
}
