import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';

import type { WorkflowTemplate, WorkflowVersion } from '@/api';
import { Badge, Button } from '@/design-system';
import { formatDateTime } from '@/lib/format';
import { ModalDialog } from '@/features/shared/components/modal-dialog';
import {
  diffWorkflowVersions,
  type WorkflowVersionDiff,
} from '@/features/workflows/lib/version-diff';

export interface VersionCompareProps {
  /** Lado "origem" (antes) do diff. */
  from: WorkflowVersion;
  /** Lado "destino" (depois) do diff. */
  to: WorkflowVersion;
  /** Template da versão de origem (nome para desambiguar os dois lados). */
  fromTemplate: WorkflowTemplate | null;
  /** Template da versão de destino (pode diferir de `fromTemplate`). */
  toTemplate: WorkflowTemplate | null;
  onClose: () => void;
}

/**
 * Comparação entre duas versões de workflow: diff ESTRUTURAL (fases
 * adicionadas/removidas/reordenadas/alteradas, gates, pesos, documentos,
 * agentes, skills, ferramentas, critérios, dependências, condições,
 * transições e modo padrão) — nunca diff de texto. Verde/vermelho seguem
 * o padrão visual do diff de documentos (+/− com Badge success/error).
 *
 * Cada lado é rotulado com nome do template + versão + data para que o
 * usuário saiba de qual workflow veio o quê (inclusive ao comparar dois
 * workflows DIFERENTES, não apenas duas versões do mesmo).
 */
export function VersionCompare({
  from,
  to,
  fromTemplate,
  toTemplate,
  onClose,
}: VersionCompareProps) {
  const { t, i18n } = useTranslation();
  const diff: WorkflowVersionDiff = useMemo(() => diffWorkflowVersions(from, to), [from, to]);

  const crossTemplate =
    fromTemplate !== null && toTemplate !== null && fromTemplate.id !== toTemplate.id;

  /** Data de publicação (ou estado de rascunho) do lado. */
  function sideDate(version: WorkflowVersion): string {
    return version.publishedAt
      ? formatDateTime(version.publishedAt, i18n.language)
      : t('workflows.templates.notPublished');
  }

  /** Rótulo compacto "Nome vN" usado nos títulos dos blocos de fases. */
  function sideTag(template: WorkflowTemplate | null, version: WorkflowVersion): string {
    const label = t('workflows.templates.version', { version: version.version });
    return template ? `${template.name} ${label}` : label;
  }

  const fromTag = sideTag(fromTemplate, from);
  const toTag = sideTag(toTemplate, to);

  return (
    <ModalDialog
      label={t('workflows.compare.title')}
      onClose={onClose}
      className="max-w-2xl"
    >
      <h3 className="font-heading text-lg font-semibold">{t('workflows.compare.title')}</h3>
      <p className="text-xs text-foreground-muted">{t('workflows.compare.subtitle')}</p>

      <div className="mt-2 grid grid-cols-[1fr_auto_1fr] items-stretch gap-2">
        <div className="rounded-lg border border-border bg-surface p-3">
          <p className="text-[11px] font-medium uppercase tracking-wide text-foreground-muted">
            {t('workflows.compare.sideFrom')}
          </p>
          <p className="text-sm font-semibold">{fromTemplate?.name}</p>
          <p className="text-xs text-foreground-muted">
            {t('workflows.templates.version', { version: from.version })} · {sideDate(from)}
          </p>
        </div>
        <div className="flex items-center text-foreground-muted" aria-hidden="true">
          →
        </div>
        <div className="rounded-lg border border-border bg-surface p-3">
          <p className="text-[11px] font-medium uppercase tracking-wide text-foreground-muted">
            {t('workflows.compare.sideTo')}
          </p>
          <p className="text-sm font-semibold">{toTemplate?.name}</p>
          <p className="text-xs text-foreground-muted">
            {t('workflows.templates.version', { version: to.version })} · {sideDate(to)}
          </p>
        </div>
      </div>

      {crossTemplate && (
        <p className="mt-2 text-xs text-warning">{t('workflows.compare.crossTemplate')}</p>
      )}

      {diff.identical ? (
        <p className="text-sm text-foreground-muted">{t('workflows.compare.identical')}</p>
      ) : (
        <div className="flex flex-col gap-4">
          {diff.phasesAdded.length > 0 && (
            <section aria-label={t('workflows.compare.phasesAddedIn', { label: toTag })}>
              <h4 className="text-sm font-semibold">
                {t('workflows.compare.phasesAddedIn', { label: toTag })}
              </h4>
              <ul className="mt-1 flex flex-wrap gap-1">
                {diff.phasesAdded.map((phase) => (
                  <li key={phase}>
                    <Badge variant="success">+ {phase}</Badge>
                  </li>
                ))}
              </ul>
            </section>
          )}

          {diff.phasesRemoved.length > 0 && (
            <section aria-label={t('workflows.compare.phasesRemovedFrom', { label: fromTag })}>
              <h4 className="text-sm font-semibold">
                {t('workflows.compare.phasesRemovedFrom', { label: fromTag })}
              </h4>
              <ul className="mt-1 flex flex-wrap gap-1">
                {diff.phasesRemoved.map((phase) => (
                  <li key={phase}>
                    <Badge variant="error">− {phase}</Badge>
                  </li>
                ))}
              </ul>
            </section>
          )}

          {diff.phasesReordered.length > 0 && (
            <p className="text-xs text-foreground-muted">
              {t('workflows.compare.phasesReordered', {
                phases: diff.phasesReordered.join(', '),
              })}
            </p>
          )}

          {diff.modeChange && (
            <p className="text-sm">
              {t('workflows.compare.modeChange', {
                from: t(`workflows.compare.mode.${diff.modeChange.from}`),
                to: t(`workflows.compare.mode.${diff.modeChange.to}`),
              })}
            </p>
          )}

          {diff.phasesChanged.length > 0 && (
            <section aria-label={t('workflows.compare.phasesChanged')}>
              <h4 className="text-sm font-semibold">{t('workflows.compare.phasesChanged')}</h4>
              <ul className="mt-2 flex flex-col gap-3">
                {diff.phasesChanged.map((phaseDiff) => (
                  <li
                    key={phaseDiff.phase}
                    className="rounded-lg border border-border bg-surface p-3"
                  >
                    <p className="text-sm font-medium">{phaseDiff.phase}</p>
                    <ul className="mt-1 flex flex-col gap-1 text-xs">
                      {phaseDiff.values.map((change) => (
                        <li key={change.field}>
                          <span className="font-medium">
                            {t(`workflows.compare.fields.${change.field}`)}:
                          </span>{' '}
                          {change.from === '' ? (
                            <span className="text-foreground-muted">—</span>
                          ) : (
                            change.from
                          )}{' '}
                          → {change.to === '' ? <span className="text-foreground-muted">—</span> : change.to}
                        </li>
                      ))}
                      {phaseDiff.lists.map((change) => (
                        <li key={change.field}>
                          <span className="font-medium">
                            {t(`workflows.compare.fields.${change.field}`)}:
                          </span>{' '}
                          {change.added.length > 0 && (
                            <span className="text-success">+ {change.added.join(', ')}</span>
                          )}
                          {change.added.length > 0 && change.removed.length > 0 && ' · '}
                          {change.removed.length > 0 && (
                            <span className="text-error">− {change.removed.join(', ')}</span>
                          )}
                        </li>
                      ))}
                    </ul>
                  </li>
                ))}
              </ul>
            </section>
          )}
        </div>
      )}

      <div className="mt-4">
        <Button type="button" variant="outline" onClick={onClose}>
          {t('common.actions.cancel')}
        </Button>
      </div>
    </ModalDialog>
  );
}
