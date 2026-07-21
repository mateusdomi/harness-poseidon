import { FlaskConical } from 'lucide-react';
import { useTranslation } from 'react-i18next';

import { Badge, Tooltip } from '@/design-system';
import { isSimulatedMode } from '@/config/features';

/**
 * Sinaliza, de forma explícita e honesta, que a tela mostra dados simulados
 * (fixtures) e não um backend real. Renderiza `null` quando a aplicação está
 * em modo `http`. Usado onde apresentamos prontidão operacional (Cockpit,
 * Orquestrador) para nunca passar dado simulado como real.
 */
export function SimulatedModeBadge({ className }: { className?: string }) {
  const { t } = useTranslation();
  if (!isSimulatedMode) return null;

  return (
    <Tooltip label={t('common.simulated.hint')} className={className}>
      <Badge variant="warning">
        <FlaskConical aria-hidden="true" className="size-3" />
        {t('common.simulated.label')}
      </Badge>
    </Tooltip>
  );
}
