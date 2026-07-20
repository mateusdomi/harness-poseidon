import { useTranslation } from 'react-i18next';
import { ShieldX } from 'lucide-react';

import { Button, Card, CardContent } from '@/design-system';

export function PermissionDenied({ onRetry }: { onRetry: () => void }) {
  const { t } = useTranslation();

  return (
    <Card role="alert" className="mx-auto max-w-2xl">
      <CardContent className="flex flex-col items-start gap-3 p-6">
        <ShieldX aria-hidden="true" className="size-8 text-error" />
        <h1 className="font-heading text-xl font-semibold">{t('common.permission.title')}</h1>
        <p className="text-sm text-foreground-muted">{t('common.permission.body')}</p>
        <Button type="button" variant="outline" onClick={onRetry}>
          {t('common.actions.retry')}
        </Button>
      </CardContent>
    </Card>
  );
}
