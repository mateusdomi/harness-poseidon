import { useTranslation } from 'react-i18next';

import { Badge } from '@/design-system';

export type InheritanceSource = 'organization' | 'productDefault';

/**
 * Sinaliza se um valor é herdado (da organização ou do padrão do produto)
 * ou sobrescrito localmente. Regra de UI: herança SEMPRE visível.
 */
export function InheritanceBadge({
  inherited,
  source,
}: {
  inherited: boolean;
  source: InheritanceSource;
}) {
  const { t } = useTranslation();
  if (inherited) {
    return (
      <Badge variant="outline">
        {source === 'organization'
          ? t('common.inheritance.inheritedFromOrg')
          : t('common.inheritance.inheritedFromDefault')}
      </Badge>
    );
  }
  return <Badge variant="info">{t('common.inheritance.overridden')}</Badge>;
}
