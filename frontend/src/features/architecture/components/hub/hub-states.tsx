import { useTranslation } from 'react-i18next';

import { Button, Skeleton } from '@/design-system';

/** Estado de carregamento padrão das abas do Hub. */
export function HubLoading() {
  const { t } = useTranslation();
  return (
    <div className="flex flex-col gap-3" role="status" aria-label={t('common.states.loading')}>
      <Skeleton className="h-10 w-full" />
      <Skeleton className="h-32 w-full" />
      <Skeleton className="h-32 w-full" />
    </div>
  );
}

/** Estado de erro padrão com ação de retry. */
export function HubError({ onRetry }: { onRetry: () => void }) {
  const { t } = useTranslation();
  return (
    <div className="flex flex-col items-start gap-3">
      <p role="alert" className="text-sm text-error">
        {t('common.states.errorBody')}
      </p>
      <Button type="button" variant="outline" onClick={onRetry}>
        {t('common.actions.retry')}
      </Button>
    </div>
  );
}

/** Mensagem de vazio consistente (aceita texto já traduzido). */
export function HubEmpty({ message }: { message: string }) {
  return <p className="py-6 text-center text-sm text-foreground-muted">{message}</p>;
}
