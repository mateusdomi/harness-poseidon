import { useTranslation } from 'react-i18next';

import { Badge, Card, CardContent, CardHeader, CardTitle, Skeleton } from '@/design-system';

const PLANNED_STATES = ['loading', 'empty', 'error', 'ready'] as const;

/**
 * Página placeholder padrão das features: mostra o nome da tela (via i18n),
 * uma descrição e os estados planejados, até a implementação real chegar.
 */
export function FeaturePlaceholderPage({ featureKey }: { featureKey: string }) {
  const { t } = useTranslation();

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="font-heading text-2xl font-semibold">{t(`features.${featureKey}.title`)}</h1>
        <Badge variant="brand">{t('placeholder.badge')}</Badge>
      </div>
      <p className="max-w-prose text-foreground-muted">{t(`features.${featureKey}.description`)}</p>

      <Card>
        <CardHeader>
          <CardTitle>{t('placeholder.statesTitle')}</CardTitle>
        </CardHeader>
        <CardContent>
          <ul className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
            {PLANNED_STATES.map((state) => (
              <li
                key={state}
                className="flex flex-col gap-2 rounded-md border border-border bg-surface-elevated p-4"
              >
                <span className="text-sm font-medium">{t(`placeholder.states.${state}`)}</span>
                <Skeleton className="h-3 w-full" />
                <Skeleton className="h-3 w-2/3" />
              </li>
            ))}
          </ul>
        </CardContent>
      </Card>
    </div>
  );
}
