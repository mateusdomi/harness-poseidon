import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import type { Approval, ResolveApprovalInput } from '@/api';
import { Badge, Button, Field, Textarea } from '@/design-system';
import { approvalStateVariant } from '@/lib/status';

export interface ApprovalResolveActionsProps {
  approval: Approval;
  /** Mutation em andamento (desabilita os botões). */
  isPending: boolean;
  onResolve: (input: ResolveApprovalInput) => void;
}

/**
 * Ações de decisão sobre uma aprovação: aprovar é um clique; reprovar
 * abre formulário inline com observação OBRIGATÓRIA (regra do contrato
 * `resolveApprovalInputSchema`). Padrão compartilhado entre o detalhe de
 * documentos e a fila consolidada de aprovações (origem: task-approvals).
 */
export function ApprovalResolveActions({
  approval,
  isPending,
  onResolve,
}: ApprovalResolveActionsProps) {
  const { t } = useTranslation();
  const [rejecting, setRejecting] = useState(false);
  const [note, setNote] = useState('');
  const [noteError, setNoteError] = useState<string | null>(null);

  function confirmReject() {
    if (note.trim().length === 0) {
      setNoteError(t('approvals.actions.noteRequired'));
      return;
    }
    onResolve({ decision: 'rejected', note: note.trim() });
  }

  if (approval.state !== 'pending') {
    return (
      <div className="flex flex-wrap items-center gap-2">
        <Badge variant={approvalStateVariant(approval.state)}>
          {t(`status.approvalState.${approval.state}`)}
        </Badge>
        {approval.resolutionNote && (
          <span className="text-xs text-foreground-muted">
            {t('approvals.actions.resolutionNote', { note: approval.resolutionNote })}
          </span>
        )}
      </div>
    );
  }

  if (rejecting) {
    return (
      <div className="flex flex-col gap-2">
        <Field
          htmlFor={`reject-note-${approval.id}`}
          label={t('approvals.actions.noteLabel')}
          required
          requiredLabel={t('common.requiredMark')}
          error={noteError ?? undefined}
        >
          <Textarea
            id={`reject-note-${approval.id}`}
            value={note}
            aria-invalid={noteError !== null}
            placeholder={t('approvals.actions.notePlaceholder')}
            onChange={(event) => {
              setNote(event.target.value);
              if (noteError) setNoteError(null);
            }}
          />
        </Field>
        <div className="flex flex-wrap gap-2">
          <Button
            type="button"
            variant="destructive"
            size="sm"
            disabled={isPending}
            onClick={confirmReject}
          >
            {t('approvals.actions.rejectConfirm')}
          </Button>
          <Button type="button" variant="outline" size="sm" onClick={() => setRejecting(false)}>
            {t('common.actions.cancel')}
          </Button>
        </div>
      </div>
    );
  }

  return (
    <div className="flex flex-wrap gap-2">
      <Button
        type="button"
        size="sm"
        disabled={isPending}
        onClick={() => onResolve({ decision: 'approved' })}
      >
        {t('approvals.actions.approve')}
      </Button>
      <Button
        type="button"
        variant="outline"
        size="sm"
        disabled={isPending}
        onClick={() => {
          setRejecting(true);
          setNote('');
          setNoteError(null);
        }}
      >
        {t('approvals.actions.reject')}
      </Button>
    </div>
  );
}
