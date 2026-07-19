import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import type { Ulid } from '@/api';
import { Button, Field, Textarea } from '@/design-system';
import {
  useResolveDocumentApproval,
  useSaveDocumentVersion,
} from '@/features/documents/hooks/use-documents';

export interface DocumentManualEditProps {
  documentId: Ulid;
  /** Conteúdo da versão vigente (ponto de partida da edição). */
  initialBody: string;
  /** Aprovação pendente do documento — habilita "Salvar e aprovar". */
  pendingApprovalId: Ulid | null;
  /** Sai do modo de edição (cancelar ou após salvar). */
  onDone: () => void;
}

/**
 * Modo de edição manual do conteúdo markdown: textarea monoespaçada sobre
 * a versão vigente. Salvar cria uma NOVA versão de origem humana (as
 * anteriores são imutáveis); "Salvar e aprovar" encadeia as duas mutações
 * (nova versão → aprovação pendente resolvida como aprovada). Cancelar
 * descarta sem tocar no documento.
 */
export function DocumentManualEdit({
  documentId,
  initialBody,
  pendingApprovalId,
  onDone,
}: DocumentManualEditProps) {
  const { t } = useTranslation();
  const saveVersion = useSaveDocumentVersion();
  const resolveApproval = useResolveDocumentApproval();
  const [body, setBody] = useState(initialBody);
  const [failed, setFailed] = useState(false);

  const busy = saveVersion.isPending || resolveApproval.isPending;
  const unchanged = body === initialBody;

  async function save() {
    setFailed(false);
    try {
      await saveVersion.mutateAsync({ documentId, body });
      onDone();
    } catch {
      setFailed(true);
    }
  }

  async function saveAndApprove() {
    if (!pendingApprovalId) return;
    setFailed(false);
    try {
      await saveVersion.mutateAsync({ documentId, body });
      await resolveApproval.mutateAsync({
        approvalId: pendingApprovalId,
        input: { decision: 'approved', note: t('documents.detail.edit.saveApproveNote') },
      });
      onDone();
    } catch {
      setFailed(true);
    }
  }

  return (
    <div className="flex flex-col gap-3">
      <Field
        htmlFor="document-edit-body"
        label={t('documents.detail.edit.bodyLabel')}
        required
        requiredLabel={t('common.requiredMark')}
      >
        <Textarea
          id="document-edit-body"
          className="min-h-72 font-mono text-sm"
          value={body}
          disabled={busy}
          onChange={(event) => setBody(event.target.value)}
        />
      </Field>
      <p className="text-xs text-foreground-muted">{t('documents.detail.edit.hint')}</p>
      <div className="flex flex-wrap gap-2">
        <Button
          type="button"
          size="sm"
          disabled={busy || unchanged || body.trim().length === 0}
          onClick={() => void save()}
        >
          {t('documents.detail.edit.save')}
        </Button>
        {pendingApprovalId && (
          <Button
            type="button"
            variant="outline"
            size="sm"
            disabled={busy || unchanged || body.trim().length === 0}
            onClick={() => void saveAndApprove()}
          >
            {t('documents.detail.edit.saveAndApprove')}
          </Button>
        )}
        <Button type="button" variant="ghost" size="sm" disabled={busy} onClick={onDone}>
          {t('common.actions.cancel')}
        </Button>
      </div>
      {failed && (
        <p role="alert" className="text-sm text-error">
          {t('documents.detail.edit.error')}
        </p>
      )}
    </div>
  );
}
