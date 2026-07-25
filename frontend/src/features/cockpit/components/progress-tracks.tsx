import { Info } from 'lucide-react';
import { useTranslation } from 'react-i18next';

import type { Progress, ProgressTrack } from '@/api';
import { Tooltip } from '@/design-system';
import { formatDateTime, formatNumber } from '@/lib/format';
import { cn } from '@/lib/utils';
import type { ProgressEvidence } from '@/features/cockpit/lib/cockpit-derive';

/**
 * Barra por trilha — cores semânticas por trilha (NUNCA a mesma cor e
 * NUNCA somadas: executado ≠ validado ≠ aprovado).
 */
const TRACK_FILL: Record<ProgressTrack, string> = {
  executed: 'bg-brand',
  validated: 'bg-info',
  approved: 'bg-success',
};

const TRACKS: readonly ProgressTrack[] = ['executed', 'validated', 'approved'];

export function ProgressTracks({
  progress,
  evidence,
}: {
  progress: Progress;
  evidence: ProgressEvidence;
}) {
  const { t } = useTranslation();
  return (
    <div className="flex flex-col gap-3">
      {TRACKS.map((track) => {
        const item = evidence.tracks[track];
        const tooltip = [
          t(`cockpit.progress.tooltips.${track}`),
          '',
          t('cockpit.progress.metadata.source'),
          t('cockpit.progress.metadata.calculation', {
            numerator: formatNumber(item.numerator),
            denominator: formatNumber(item.denominator),
          }),
          t('cockpit.progress.metadata.pending', { count: item.pendingItems }),
          evidence.updatedAt
            ? t('cockpit.progress.metadata.updatedAt', {
                value: formatDateTime(evidence.updatedAt),
              })
            : t('cockpit.progress.metadata.noUpdate'),
        ].join('\n');

        return (
        <div key={track} className="flex flex-col gap-1.5">
          <div className="flex items-center justify-between gap-2 text-sm">
            <span className="flex items-center gap-1.5 text-foreground-muted">
              {t(`cockpit.progress.tracks.${track}`)}
              <Tooltip label={tooltip}>
                <button
                  type="button"
                  aria-label={tooltip}
                  className="rounded-full text-foreground-muted hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                >
                  <Info aria-hidden="true" className="size-3.5" />
                </button>
              </Tooltip>
            </span>
            <span className="font-medium tabular-nums">{formatNumber(progress[track])}%</span>
          </div>
          <div
            role="progressbar"
            aria-label={t(`cockpit.progress.tracks.${track}`)}
            aria-valuenow={progress[track]}
            aria-valuemin={0}
            aria-valuemax={100}
            className="h-2 overflow-hidden rounded-full bg-surface-elevated"
          >
            <div
              className={cn('h-full rounded-full transition-[width]', TRACK_FILL[track])}
              style={{ width: `${progress[track]}%` }}
            />
          </div>
          <div className="flex flex-wrap justify-between gap-x-3 gap-y-1 text-xs text-foreground-muted">
            <span>
              {t('cockpit.progress.metadata.fraction', {
                numerator: formatNumber(item.numerator),
                denominator: formatNumber(item.denominator),
              })}
            </span>
            <span>{t('cockpit.progress.metadata.pending', { count: item.pendingItems })}</span>
          </div>
        </div>
        );
      })}
      <p className="text-xs text-foreground-muted">
        {t('cockpit.progress.metadata.source')}
        {' · '}
        {evidence.updatedAt
          ? t('cockpit.progress.metadata.updatedAt', {
              value: formatDateTime(evidence.updatedAt),
            })
          : t('cockpit.progress.metadata.noUpdate')}
      </p>
    </div>
  );
}
