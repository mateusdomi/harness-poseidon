import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import type { ComponentState, Ulid } from '@/api';
import { Button } from '@/design-system';
import { ModalDialog } from '@/features/shared/components/modal-dialog';
import {
  useSetComponentState,
  type CatalogResource,
} from '@/features/tools/hooks/use-tools';
import { nextComponentState, toggleAction } from '@/features/tools/lib/tools-derive';

export interface ComponentToggleProps {
  /** Recurso do catálogo (`tools`, `skills`, `plugins` ou `mcp-servers`). */
  resource: CatalogResource;
  id: Ulid;
  /** Nome do item, exibido no título do dialog de confirmação. */
  name: string;
  state: ComponentState;
}

/**
 * Botão habilitar/desabilitar com confirmação: o dialog mostra o nome do
 * item, a ação e um aviso quando o item está em `error` (a alternância
 * não resolve a causa do erro). Item em erro também pode alternar estado.
 */
export function ComponentToggle({ resource, id, name, state }: ComponentToggleProps) {
  const { t } = useTranslation();
  const setComponentState = useSetComponentState();
  const [dialogOpen, setDialogOpen] = useState(false);
  const action = toggleAction(state);
  const actionLabel = t(`tools.actions.${action}`);

  function confirm() {
    setComponentState.mutate(
      { resource, id, state: nextComponentState(state) },
      { onSuccess: () => setDialogOpen(false) },
    );
  }

  return (
    <>
      <Button type="button" variant="outline" size="sm" onClick={() => setDialogOpen(true)}>
        {actionLabel}
      </Button>

      {dialogOpen && (
        <ModalDialog
          label={t(`tools.dialog.${action}Title`, { name })}
          onClose={() => setDialogOpen(false)}
        >
          <h3 className="font-heading text-lg font-semibold">
            {t(`tools.dialog.${action}Title`, { name })}
          </h3>

          <p className="text-sm text-foreground-muted">{t(`tools.dialog.${action}Body`)}</p>

          {state === 'error' && (
            <p className="rounded-lg border border-border bg-surface p-3 text-xs text-foreground-muted">
              {t('tools.dialog.errorWarning')}
            </p>
          )}

          {setComponentState.isError && (
            <p role="alert" className="text-xs text-error">
              {t('tools.dialog.mutationError')}
            </p>
          )}

          <div className="flex flex-wrap gap-2">
            <Button type="button" disabled={setComponentState.isPending} onClick={confirm}>
              {actionLabel}
            </Button>
            <Button type="button" variant="outline" onClick={() => setDialogOpen(false)}>
              {t('common.actions.cancel')}
            </Button>
          </div>
        </ModalDialog>
      )}
    </>
  );
}
