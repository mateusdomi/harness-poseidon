import { useTranslation } from 'react-i18next';

import { cn } from '@/lib/utils';
import { consumptionPct, consumptionTone, type ConsumptionTone } from '@/features/providers/lib/providers-derive';

const TONE_CLASSES: Record<ConsumptionTone, string> = {
  brand: 'bg-brand',
  warning: 'bg-warning',
  error: 'bg-error',
};

export interface ConsumptionBarProps {
  used: number;
  limit: number | null;
  /** Limiar de alerta (%) — vira marcador na barra e muda o tom. */
  alertThresholdPct?: number;
  label: string;
}

/** Barra de consumo (cota/budget) com marcador do limiar de alerta. */
export function ConsumptionBar({ used, limit, alertThresholdPct = 80, label }: ConsumptionBarProps) {
  const { t } = useTranslation();
  const pct = consumptionPct(used, limit);
  const tone = consumptionTone(pct, alertThresholdPct);
  const width = pct === null ? 0 : Math.min(pct, 100);

  return (
    <div className="flex flex-col gap-1">
      <div
        role="progressbar"
        aria-label={label}
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={pct === null ? 0 : Math.round(pct)}
        aria-valuetext={
          pct === null ? t('providers.bars.noLimit') : t('providers.bars.pct', { pct: Math.round(pct) })
        }
        className="relative h-2 w-full overflow-hidden rounded-full bg-surface-elevated"
      >
        <div
          className={cn('h-full rounded-full transition-all', TONE_CLASSES[tone])}
          style={{ width: `${width}%` }}
        />
        {limit !== null && (
          <span
            aria-hidden="true"
            className="absolute inset-y-0 w-0.5 bg-foreground-muted"
            style={{ left: `${alertThresholdPct}%` }}
          />
        )}
      </div>
    </div>
  );
}
