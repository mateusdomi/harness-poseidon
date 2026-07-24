import { useTranslation } from 'react-i18next';

import { Badge, type BadgeProps } from '@/design-system';

type Variant = BadgeProps['variant'];

const HEALTH_VARIANT: Record<string, Variant> = {
  green: 'success',
  yellow: 'warning',
  red: 'error',
};

const PREDICTABILITY_VARIANT: Record<string, Variant> = {
  on_track: 'success',
  at_risk: 'warning',
  off_track: 'error',
  unknown: 'outline',
};

const CONFIDENCE_VARIANT: Record<string, Variant> = {
  high: 'success',
  medium: 'warning',
  low: 'error',
};

const INDICATOR_VARIANT: Record<string, Variant> = {
  good: 'success',
  watch: 'warning',
  bad: 'error',
  unknown: 'outline',
};

const SEVERITY_VARIANT: Record<string, Variant> = {
  critical: 'error',
  warning: 'warning',
  info: 'info',
};

const REPORT_STATUS_VARIANT: Record<string, Variant> = {
  draft: 'outline',
  approved: 'info',
  sent: 'success',
};

/** Rótulo traduzido de um valor de enum, com fallback para o próprio código. */
function useEnumLabel() {
  const { t } = useTranslation();
  return (group: string, value: string) => {
    const key = `delivery.${group}.${value}`;
    const label = t(key);
    return label === key ? value : label;
  };
}

export function HealthBadge({ value }: { value: string }) {
  const label = useEnumLabel();
  return <Badge variant={HEALTH_VARIANT[value] ?? 'outline'}>{label('health', value)}</Badge>;
}

export function PredictabilityBadge({ value }: { value: string }) {
  const label = useEnumLabel();
  return (
    <Badge variant={PREDICTABILITY_VARIANT[value] ?? 'outline'}>
      {label('predictability', value)}
    </Badge>
  );
}

export function ConfidenceBadge({ value }: { value: string }) {
  const label = useEnumLabel();
  return <Badge variant={CONFIDENCE_VARIANT[value] ?? 'outline'}>{label('confidence', value)}</Badge>;
}

export function IndicatorStatusBadge({ value }: { value: string }) {
  const label = useEnumLabel();
  return (
    <Badge variant={INDICATOR_VARIANT[value] ?? 'outline'}>{label('indicatorStatus', value)}</Badge>
  );
}

export function SeverityBadge({ value }: { value: string }) {
  const label = useEnumLabel();
  return <Badge variant={SEVERITY_VARIANT[value] ?? 'outline'}>{label('severity', value)}</Badge>;
}

export function ReportStatusBadge({ value }: { value: string }) {
  const label = useEnumLabel();
  return (
    <Badge variant={REPORT_STATUS_VARIANT[value] ?? 'outline'}>{label('reports.status', value)}</Badge>
  );
}
