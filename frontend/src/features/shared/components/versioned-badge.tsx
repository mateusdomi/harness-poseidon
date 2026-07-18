import { useTranslation } from 'react-i18next';

import { Badge } from '@/design-system';

/**
 * Marca campos versionados da configuração do projeto (repositório,
 * tecnologias, marca): mudanças incrementam `configVersion`.
 */
export function VersionedBadge() {
  const { t } = useTranslation();
  return (
    <Badge variant="default" title={t('common.versioned.tooltip')}>
      {t('common.versioned.badge')}
    </Badge>
  );
}
