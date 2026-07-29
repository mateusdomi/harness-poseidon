import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';

import type { Approval } from '@/api';
import { Badge, Button, Field, Textarea } from '@/design-system';
import { formatDateTime } from '@/lib/format';
import { approvalStateVariant, priorityVariant } from '@/lib/status';
import { useResolveTaskApproval } from '@/features/board/hooks/use-board';
import { approvalPresentation } from '@/features/board/lib/board-presentation';

export interface TaskApprovalsProps {
  approvals: Approval[];
  showTechnicalDetails?: boolean;
}

/**
 * Gates/aprovações ligadas à tarefa. Aprovar é um clique; reprovar abre
 * formulário inline com observação OBRIGATÓRIA (regra do contrato
 * `resolveApprovalInputSchema`).
 */
export function TaskApprovals({
  approvals,
  showTechnicalDetails = false,
}: TaskApprovalsProps) {
  const { t, i18n } = useTranslation();
  const resolveApproval = useResolveTaskApproval();
  const [rejectingId, setRejectingId] = useState<string | null>(null);
  const [note, setNote] = useState('');
  const [noteError, setNoteError] = useState<string | null>(null);

  function startReject(approvalId: string) {
    setRejectingId(approvalId);
    setNote('');
    setNoteError(null);
  }

  function confirmReject(approvalId: string) {
    if (note.trim().length === 0) {
      setNoteError(t('board.detail.approvals.noteRequired'));
      return;
    }
    resolveApproval.mutate(
      { approvalId, input: { decision: 'rejected', note: note.trim() } },
      { onSuccess: () => setRejectingId(null) },
    );
  }

  if (approvals.length === 0) {
    return <p className="text-sm text-foreground-muted">{t('board.detail.approvals.empty')}</p>;
  }

  return (
    <ul className="flex flex-col gap-3">
      {approvals.map((approval) => {
        const presentation = approvalPresentation(
          approval,
          showTechnicalDetails,
          t,
        );
        return (
          <li
            key={approval.id}
            className="flex flex-col gap-2 rounded-lg border border-border bg-surface p-3"
          >
            <div className="flex flex-wrap items-center gap-2">
              <span className="text-sm font-medium">{presentation.title}</span>
              {presentation.kindKey && (
                <Badge variant="brand">{t(presentation.kindKey)}</Badge>
              )}
              <Badge variant={approvalStateVariant(approval.state)}>
                {t(`status.approvalState.${approval.state}`)}
              </Badge>
              <Badge variant={priorityVariant(approval.priority)}>
                {t(`status.priority.${approval.priority}`)}
              </Badge>
            </div>
            <p className="text-xs text-foreground-muted">{presentation.description}</p>
            {presentation.resolutionNote && (
              <p className="text-xs text-foreground-muted">
                {t('board.detail.approvals.resolutionNote', {
                  note: presentation.resolutionNote,
                })}
              </p>
            )}
            {presentation.hidesOperationalNote && (
              <p className="text-xs text-foreground-muted">
                {t('board.detail.approvals.businessResolutionRecorded')}
              </p>
            )}
            <p className="text-xs text-foreground-muted">
              {t('board.detail.approvals.requestedAt', {
                date: formatDateTime(approval.requestedAt, i18n.language),
              })}
            </p>
            {approval.resolvedAt && (
              <p className="text-xs text-foreground-muted">
                {formatDateTime(approval.resolvedAt, i18n.language)}
              </p>
            )}

            {approval.state === 'pending' && presentation.canResolve && (
              <>
                {rejectingId === approval.id ? (
                  <div className="flex flex-col gap-2">
                    <Field
                      htmlFor={`reject-note-${approval.id}`}
                      label={t('board.detail.approvals.noteLabel')}
                      required
                      requiredLabel={t('common.requiredMark')}
                      error={noteError ?? undefined}
                    >
                      <Textarea
                        id={`reject-note-${approval.id}`}
                        value={note}
                        aria-invalid={noteError !== null}
                        placeholder={t('board.detail.approvals.notePlaceholder')}
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
                        disabled={resolveApproval.isPending}
                        onClick={() => confirmReject(approval.id)}
                      >
                        {t('board.detail.approvals.rejectConfirm')}
                      </Button>
                      <Button
                        type="button"
                        variant="outline"
                        size="sm"
                        onClick={() => setRejectingId(null)}
                      >
                        {t('common.actions.cancel')}
                      </Button>
                    </div>
                  </div>
                ) : (
                  <div className="flex flex-wrap gap-2">
                    <Button
                      type="button"
                      size="sm"
                      disabled={resolveApproval.isPending}
                      onClick={() =>
                        resolveApproval.mutate({
                          approvalId: approval.id,
                          input: { decision: 'approved' },
                        })
                      }
                    >
                      {t('board.detail.approvals.approve')}
                    </Button>
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      disabled={resolveApproval.isPending}
                      onClick={() => startReject(approval.id)}
                    >
                      {t('board.detail.approvals.reject')}
                    </Button>
                  </div>
                )}
              </>
            )}
            {approval.state === 'pending' && !presentation.canResolve && (
              <Link
                to="/chat"
                className="text-sm font-medium text-brand-strong underline-offset-4 hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
              >
                {t('board.detail.approvals.askForContext')}
              </Link>
            )}
          </li>
        );
      })}
    </ul>
  );
}
