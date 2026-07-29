import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';

import { Button, Card, CardContent, Skeleton } from '@/design-system';

export const ROUTE_SKELETON_TIMEOUT_MS = 10_000;

/** Fallback de Suspense para rotas lazy: esqueleto de página. */
export function RouteSkeleton() {
  const { t } = useTranslation();
  const [timedOut, setTimedOut] = useState(false);
  useEffect(() => {
    const timer = setTimeout(() => setTimedOut(true), ROUTE_SKELETON_TIMEOUT_MS);
    return () => clearTimeout(timer);
  }, []);
  if (timedOut) {
    return (
      <Card>
        <CardContent className="flex flex-col items-start gap-3 p-6">
          <p role="alert" className="text-sm text-error">{t('common.requests.routeTimeout')}</p>
          <Button type="button" variant="outline" onClick={() => window.location.reload()}>
            {t('common.actions.retry')}
          </Button>
        </CardContent>
      </Card>
    );
  }
  return (
    <div className="flex flex-col gap-6" role="status" aria-busy="true">
      <Skeleton className="h-8 w-48" />
      <Skeleton className="h-4 w-full max-w-prose" />
      <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-4">
        <Skeleton className="h-24 w-full" />
        <Skeleton className="h-24 w-full" />
        <Skeleton className="h-24 w-full" />
        <Skeleton className="h-24 w-full" />
      </div>
    </div>
  );
}
