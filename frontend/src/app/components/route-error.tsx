import { Link, isRouteErrorResponse, useRouteError } from 'react-router-dom';
import { useTranslation } from 'react-i18next';

import { Button, Card, CardContent, CardHeader, CardTitle } from '@/design-system';

/** Mensagem legível do erro capturado pela rota, sem vazar stack para a UI. */
function describe(error: unknown): string | null {
  if (isRouteErrorResponse(error)) return `${error.status} ${error.statusText}`;
  if (error instanceof Error) return error.message;
  return null;
}

/** Um endereço que não corresponde a nenhuma tela — não é falha de renderização. */
function isNotFound(error: unknown): boolean {
  return isRouteErrorResponse(error) && error.status === 404;
}

/**
 * `errorElement` por rota: substitui a tela crua do react-router
 * ("Unexpected Application Error!") por uma superfície acessível e traduzida,
 * com ação de recuperação. Um erro em uma rota nunca deve derrubar o shell.
 *
 * Endereço inexistente é tratado à parte: chamá-lo de "erro inesperado" e oferecer
 * "recarregar" descreve mal o que houve e oferece uma saída que nunca funciona —
 * recarregar a mesma URL errada dá exatamente o mesmo resultado. O caminho útil é
 * voltar para uma tela que existe.
 */
export function RouteError() {
  const { t } = useTranslation();
  const error = useRouteError();
  const notFound = isNotFound(error);
  const detail = notFound ? null : describe(error);
  return (
    <Card>
      <CardHeader>
        <CardTitle>
          {t(notFound ? 'common.routeError.notFoundTitle' : 'common.routeError.title')}
        </CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col items-start gap-3">
        <p role="alert" className="max-w-prose text-sm text-foreground-muted">
          {t(notFound ? 'common.routeError.notFoundBody' : 'common.routeError.body')}
        </p>
        {detail ? (
          <p className="max-w-prose break-words text-xs text-foreground-muted/80">{detail}</p>
        ) : null}
        {notFound ? (
          <Button asChild variant="outline">
            <Link to="/">{t('common.routeError.backHome')}</Link>
          </Button>
        ) : (
          <Button type="button" variant="outline" onClick={() => window.location.reload()}>
            {t('common.routeError.reload')}
          </Button>
        )}
      </CardContent>
    </Card>
  );
}
