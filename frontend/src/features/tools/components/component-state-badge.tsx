import { useTranslation } from 'react-i18next';

import type { ComponentState } from '@/api';
import { Badge } from '@/design-system';
import { componentStateVariant } from '@/lib/status';

/**
 * Badge de estado operacional (enabled/disabled/error) de ferramentas,
 * skills, plugins e servidores MCP — variante semântica via `@/lib/status`.
 */
export function ComponentStateBadge({ state }: { state: ComponentState }) {
  const { t } = useTranslation();
  return (
    <Badge variant={componentStateVariant(state)}>{t(`status.componentState.${state}`)}</Badge>
  );
}
