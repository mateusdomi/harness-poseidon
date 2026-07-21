import { useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { AlertTriangle, Check, Copy, FileCheck, Pencil } from 'lucide-react';

import type { Approval, DocumentState, DocumentVersion, Ulid } from '@/api';
import { Badge, Button, Select, Skeleton } from '@/design-system';
import { formatDateTime } from '@/lib/format';
import { documentStateVariant } from '@/lib/status';
import { ApprovalResolveActions } from '@/features/shared/components/approval-resolve-actions';
import { BackLink } from '@/features/shared/components/back-link';
import { MarkdownContent } from '@/features/shared/components/markdown-content';
import { DocumentManualEdit } from '@/features/documents/components/document-manual-edit';
import { diffLines } from '@/features/documents/lib/diff';
import {
  useDocumentDetail,
  useRequestDocumentApproval,
  useResolveDocumentApproval,
} from '@/features/documents/hooks/use-documents';

/** Tempo do feedback visual "copiado" (mesmo padrão da cópia do chat). */
const COPIED_FEEDBACK_MS = 1600;

/**
 * Estados em que a edição manual faz sentido (elaboração/revisão/correção
 * antes da aprovação). Documento aprovado/terminal exige reabrir o fluxo —
 * a UI não oferece edição (decisão D-072).
 */
const EDITABLE_STATES: readonly DocumentState[] = ['inElaboration', 'inReview', 'awaitingApproval'];

export interface DocumentDetailProps {
  documentId: Ulid;
  /** Aprovações do projeto (localiza a pendente deste documento). */
  approvals: Approval[];
  /** Chefe do projeto (autor do pedido de aprovação). */
  chiefAgentId: Ulid;
  onBack: () => void;
}

/**
 * Detalhe do documento: metadados (estado, classificações, inconsistência/
 * waiver), conteúdo markdown da versão vigente com cópia (clipboard) e
 * edição manual (nova versão de origem humana; "salvar e aprovar" encadeia
 * versão + aprovação pendente), histórico de versões com origem (autor) e
 * comparação (diff linha a linha) e ações de aprovação (reprovação exige
 * observação — regra do contrato; reprovar = solicitar correção).
 */
export function DocumentDetail({ documentId, approvals, chiefAgentId, onBack }: DocumentDetailProps) {
  const { t, i18n } = useTranslation();
  const { document, versions, isPending, isError, refetch } = useDocumentDetail(documentId);
  const resolveApproval = useResolveDocumentApproval();
  const requestApproval = useRequestDocumentApproval();

  const [diffFrom, setDiffFrom] = useState<number | null>(null);
  const [diffTo, setDiffTo] = useState<number | null>(null);
  const [editing, setEditing] = useState(false);
  const [copied, setCopied] = useState(false);
  const feedbackTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(
    () => () => {
      if (feedbackTimer.current) clearTimeout(feedbackTimer.current);
    },
    [],
  );

  const pendingApproval = approvals.find(
    (approval) => approval.documentId === documentId && approval.state === 'pending',
  );
  // Sem pendente, mostra a decisão mais recente (ex.: recém-reprovada).
  const latestResolved = approvals
    .filter((approval) => approval.documentId === documentId && approval.state !== 'pending')
    .sort((a, b) => (b.resolvedAt ?? '').localeCompare(a.resolvedAt ?? ''))[0];
  const shownApproval = pendingApproval ?? latestResolved;

  const diff = useMemo(() => {
    if (diffFrom === null || diffTo === null) return null;
    const from = versions.find((v) => v.version === diffFrom);
    const to = versions.find((v) => v.version === diffTo);
    if (!from || !to) return null;
    return diffLines(from.body, to.body);
  }, [versions, diffFrom, diffTo]);

  if (isPending) {
    return (
      <div className="flex flex-col gap-3" role="status" aria-label={t('common.states.loading')}>
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-64 w-full" />
      </div>
    );
  }

  if (isError || !document) {
    return (
      <div className="flex flex-col items-start gap-3">
        <p role="alert" className="text-sm text-error">
          {t('common.states.errorBody')}
        </p>
        <Button type="button" variant="outline" onClick={refetch}>
          {t('common.actions.retry')}
        </Button>
      </div>
    );
  }

  const currentVersion: DocumentVersion | undefined = versions.find(
    (v) => v.version === document.currentVersion,
  );
  const editable = EDITABLE_STATES.includes(document.state) && currentVersion !== undefined;

  async function copyContent() {
    if (!currentVersion) return;
    try {
      await navigator.clipboard.writeText(currentVersion.body);
      setCopied(true);
      if (feedbackTimer.current) clearTimeout(feedbackTimer.current);
      feedbackTimer.current = setTimeout(() => setCopied(false), COPIED_FEEDBACK_MS);
    } catch {
      // Clipboard indisponível (permissão negada/contexto inseguro): sem feedback falso.
    }
  }

  return (
    <article className="flex flex-col gap-6">
      <div className="flex flex-col gap-3">
        {/* Padrão compartilhado de volta (§5): seta, rótulo específico e
            fallback para a rota-pai quando não há origem no histórico. */}
        <BackLink label={t('documents.detail.back')} onBack={onBack} fallbackTo="/documents" />
        <div className="flex flex-wrap items-center gap-2">
          <h1 className="font-heading text-2xl font-semibold">{document.title}</h1>
          <Badge variant="outline">{t(`status.documentKind.${document.kind}`)}</Badge>
          <Badge variant={documentStateVariant(document.state)}>
            {t(`status.documentState.${document.state}`)}
          </Badge>
          <Badge variant="outline">
            {t('documents.detail.version', { version: document.currentVersion })}
          </Badge>
        </div>
        {document.classifications.length > 0 && (
          <div className="flex flex-wrap gap-1">
            {document.classifications.map((classification) => (
              <Badge key={classification} variant="default">
                {classification}
              </Badge>
            ))}
          </div>
        )}
        {document.phaseName && (
          <p className="text-xs text-foreground-muted">
            {t('documents.detail.phase', { phase: document.phaseName })}
          </p>
        )}
        {document.inconsistent && (
          <p className="flex items-start gap-2 rounded-lg border border-border bg-surface p-3 text-xs text-foreground-muted">
            <AlertTriangle aria-hidden="true" className="mt-0.5 size-4 shrink-0" />
            {t('documents.detail.inconsistent')}
          </p>
        )}
        {document.waiver && (
          <p className="flex items-start gap-2 rounded-lg border border-border bg-surface p-3 text-xs text-foreground-muted">
            <FileCheck aria-hidden="true" className="mt-0.5 size-4 shrink-0" />
            {t('documents.detail.waiver', {
              reason: document.waiver.reason,
              date: formatDateTime(document.waiver.grantedAt, i18n.language),
            })}
          </p>
        )}
      </div>

      <section aria-labelledby="document-content" className="flex flex-col gap-2">
        <div className="flex flex-wrap items-center gap-2">
          <h2 id="document-content" className="font-heading text-lg font-semibold">
            {t('documents.detail.contentTitle')}
          </h2>
          {!editing && currentVersion && (
            <div className="ml-auto flex flex-wrap gap-2">
              <Button
                type="button"
                variant="outline"
                size="sm"
                aria-label={t('documents.detail.copy')}
                onClick={() => void copyContent()}
              >
                {copied ? (
                  <Check aria-hidden="true" className="text-success" />
                ) : (
                  <Copy aria-hidden="true" />
                )}
                {copied ? t('documents.detail.copied') : t('documents.detail.copy')}
              </Button>
              {editable && (
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  onClick={() => setEditing(true)}
                >
                  <Pencil aria-hidden="true" />
                  {t('documents.detail.edit.open')}
                </Button>
              )}
            </div>
          )}
        </div>
        {/* Feedback da cópia anunciado a leitores de tela (aria-label estável). */}
        <span aria-live="polite" className="sr-only">
          {copied ? t('documents.detail.copied') : ''}
        </span>
        {editing && currentVersion ? (
          <DocumentManualEdit
            documentId={document.id}
            initialBody={currentVersion.body}
            pendingApprovalId={pendingApproval?.id ?? null}
            onDone={() => setEditing(false)}
          />
        ) : (
          <div className="rounded-xl border border-border bg-surface p-4">
            {currentVersion ? (
              <MarkdownContent content={currentVersion.body} />
            ) : (
              <p className="text-sm text-foreground-muted">{t('documents.detail.noContent')}</p>
            )}
          </div>
        )}
      </section>

      <section aria-labelledby="document-approval" className="flex flex-col gap-2">
        <h2 id="document-approval" className="font-heading text-lg font-semibold">
          {t('documents.detail.approvalTitle')}
        </h2>
        {shownApproval ? (
          <ApprovalResolveActions
            approval={shownApproval}
            isPending={resolveApproval.isPending}
            onResolve={(input) =>
              resolveApproval.mutate({ approvalId: shownApproval.id, input })
            }
          />
        ) : document.state === 'inReview' ? (
          <div className="flex flex-col items-start gap-2">
            <p className="text-sm text-foreground-muted">{t('documents.detail.requestHint')}</p>
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={requestApproval.isPending}
              onClick={() =>
                requestApproval.mutate({
                  document,
                  requestedByAgentId: chiefAgentId,
                  title: t('documents.detail.requestTitle', { title: document.title }),
                  description: t('documents.detail.requestDescription', { title: document.title }),
                })
              }
            >
              {t('documents.detail.requestApproval')}
            </Button>
          </div>
        ) : (
          <p className="text-sm text-foreground-muted">{t('documents.detail.noPendingApproval')}</p>
        )}
      </section>

      <section aria-labelledby="document-versions" className="flex flex-col gap-2">
        <h2 id="document-versions" className="font-heading text-lg font-semibold">
          {t('documents.detail.versionsTitle')}
        </h2>
        <ul className="flex flex-col gap-1">
          {versions.map((version) => (
            <li key={version.id} className="flex flex-wrap items-center gap-2 text-xs text-foreground-muted">
              <span className="font-medium text-foreground">
                {t('documents.detail.version', { version: version.version })}
              </span>
              <span>{t(`documents.detail.authorKind.${version.authorKind}`)}</span>
              <span>{formatDateTime(version.createdAt, i18n.language)}</span>
            </li>
          ))}
        </ul>

        {versions.length >= 2 && (
          <div className="mt-2 flex flex-col gap-3">
            <div className="flex flex-wrap items-end gap-3">
              <div className="flex flex-col gap-1">
                <label htmlFor="diff-from" className="text-xs font-medium">
                  {t('documents.detail.diffFrom')}
                </label>
                <Select
                  id="diff-from"
                  value={diffFrom === null ? '' : String(diffFrom)}
                  onChange={(event) =>
                    setDiffFrom(event.target.value === '' ? null : Number(event.target.value))
                  }
                >
                  <option value="">{t('documents.detail.diffSelect')}</option>
                  {versions.map((version) => (
                    <option key={version.id} value={version.version}>
                      {t('documents.detail.version', { version: version.version })}
                    </option>
                  ))}
                </Select>
              </div>
              <div className="flex flex-col gap-1">
                <label htmlFor="diff-to" className="text-xs font-medium">
                  {t('documents.detail.diffTo')}
                </label>
                <Select
                  id="diff-to"
                  value={diffTo === null ? '' : String(diffTo)}
                  onChange={(event) =>
                    setDiffTo(event.target.value === '' ? null : Number(event.target.value))
                  }
                >
                  <option value="">{t('documents.detail.diffSelect')}</option>
                  {versions.map((version) => (
                    <option key={version.id} value={version.version}>
                      {t('documents.detail.version', { version: version.version })}
                    </option>
                  ))}
                </Select>
              </div>
            </div>

            {diff && (
              <div className="overflow-x-auto rounded-lg border border-border">
                {diff.identical ? (
                  <p className="p-3 text-sm text-foreground-muted">
                    {t('documents.detail.diffIdentical')}
                  </p>
                ) : (
                  <>
                    <p className="border-b border-border bg-surface p-2 text-xs text-foreground-muted">
                      {t('documents.detail.diffSummary', {
                        added: diff.addedCount,
                        removed: diff.removedCount,
                      })}
                    </p>
                    <table className="w-full border-collapse font-mono text-xs">
                      <tbody>
                        {diff.lines.map((line, index) => (
                          <tr
                            key={index}
                            className={
                              line.type === 'added'
                                ? 'bg-success/10'
                                : line.type === 'removed'
                                  ? 'bg-error/10'
                                  : undefined
                            }
                          >
                            <td className="w-10 select-none px-2 py-0.5 text-right text-foreground-muted">
                              {line.oldLine ?? ''}
                            </td>
                            <td className="w-10 select-none px-2 py-0.5 text-right text-foreground-muted">
                              {line.newLine ?? ''}
                            </td>
                            <td className="whitespace-pre-wrap px-2 py-0.5">
                              {line.type === 'added' ? '+ ' : line.type === 'removed' ? '− ' : '  '}
                              {line.text}
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </>
                )}
              </div>
            )}
          </div>
        )}
      </section>
    </article>
  );
}
