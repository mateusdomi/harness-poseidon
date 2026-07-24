import { Info } from 'lucide-react';
import { useTranslation } from 'react-i18next';

import type { Progress, ProgressTrack } from '@/api';
import { Tooltip } from '@/design-system';
import { formatNumber } from '@/lib/format';
import { cn } from '@/lib/utils';

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

export function ProgressTracks({ progress }: { progress: Progress }) {
  const { t } = useTranslation();
  return (
    <div className="flex flex-col gap-3">
      {TRACKS.map((track) => (
        <div key={track} className="flex flex-col gap-1">
          <div className="flex items-center justify-between gap-2 text-sm">
            <span className="flex items-center gap-1.5 text-foreground-muted">
              {t(`cockpit.progress.tracks.${track}`)}
              <Tooltip label={t(`cockpit.progress.tooltips.${track}`)}>
                <button
                  type="button"
                  aria-label={t(`cockpit.progress.tooltips.${track}`)}
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
        </div>
      ))}
    </div>
  );
}
