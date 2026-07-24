import { isRouteErrorResponse, useRouteError } from 'react-router-dom';
import { useTranslation } from 'react-i18next';

import { Button, Card, CardContent, CardHeader, CardTitle } from '@/design-system';

/** Mensagem legível do erro capturado pela rota, sem vazar stack para a UI. */
function describe(error: unknown): string | null {
  if (isRouteErrorResponse(error)) return `${error.status} ${error.statusText}`;
  if (error instanceof Error) return error.message;
  return null;
}

/**
 * `errorElement` por rota: substitui a tela crua do react-router
 * ("Unexpected Application Error!") por uma superfície acessível e traduzida,
 * com ação de recuperação. Um erro em uma rota nunca deve derrubar o shell.
 */
export function RouteError() {
  const { t } = useTranslation();
  const error = useRouteError();
  const detail = describe(error);
  return (
    <Card>
      <CardHeader>
        <CardTitle>{t('common.routeError.title')}</CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col items-start gap-3">
        <p role="alert" className="max-w-prose text-sm text-foreground-muted">
          {t('common.routeError.body')}
        </p>
        {detail ? (
          <p className="max-w-prose break-words text-xs text-foreground-muted/80">{detail}</p>
        ) : null}
        <Button type="button" variant="outline" onClick={() => window.location.reload()}>
          {t('common.routeError.reload')}
        </Button>
      </CardContent>
    </Card>
  );
}
