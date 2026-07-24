import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Pencil, Save, Trash2, X, RotateCw } from 'lucide-react';

import { ApiError } from '@/api';
import { Button, Card, CardContent, Skeleton, Textarea } from '@/design-system';
import { MarkdownContent } from '@/features/shared/components/markdown-content';
import { DocTree } from '@/features/governance-docs/components/doc-tree';
import {
  useDeleteGovernanceDoc,
  useGovernanceDoc,
  useGovernanceDocsTree,
  useSaveGovernanceDoc,
} from '@/features/governance-docs/hooks/use-governance-docs';
import { buildDocTree, isMarkdown } from '@/features/governance-docs/lib/tree';

/**
 * Documentos de Governança: árvore de arquivos em disco (allowlist
 * `governance/`, `docs/`) com visualização (markdown/texto), edição e
 * exclusão. Editar/excluir escreve no disco e passa a valer para o Chefe.
 */
export default function GovernanceDocsPage() {
  const { t } = useTranslation();
  const treeQuery = useGovernanceDocsTree();
  const [selectedPath, setSelectedPath] = useState<string | null>(null);
  const docQuery = useGovernanceDoc(selectedPath);
  const saveMutation = useSaveGovernanceDoc();
  const deleteMutation = useDeleteGovernanceDoc();

  const [mode, setMode] = useState<'view' | 'edit'>('view');
  const [draft, setDraft] = useState('');
  const [confirmingDelete, setConfirmingDelete] = useState(false);

  const nodes = useMemo(
    () => buildDocTree(treeQuery.data?.files ?? []),
    [treeQuery.data?.files],
  );

  // Ao trocar de documento (ou carregar), volta para visualização e sincroniza o rascunho.
  useEffect(() => {
    setMode('view');
    setConfirmingDelete(false);
  }, [selectedPath]);

  useEffect(() => {
    if (docQuery.data) setDraft(docQuery.data.content);
  }, [docQuery.data]);

  function selectPath(path: string) {
    setSelectedPath(path);
  }

  async function handleSave() {
    if (!selectedPath) return;
    await saveMutation.mutateAsync({ path: selectedPath, content: draft });
    setMode('view');
  }

  async function handleDelete() {
    if (!selectedPath) return;
    await deleteMutation.mutateAsync(selectedPath);
    setSelectedPath(null);
    setConfirmingDelete(false);
  }

  const dirty = docQuery.data ? draft !== docQuery.data.content : false;

  return (
    <div className="flex flex-col gap-4">
      <header className="flex flex-col gap-1">
        <h1 className="font-heading text-2xl font-semibold">{t('governanceDocs.title')}</h1>
        <p className="max-w-3xl text-sm text-foreground-muted">{t('governanceDocs.subtitle')}</p>
      </header>

      <div className="grid gap-4 lg:grid-cols-[minmax(14rem,20rem)_1fr]">
        <Card className="h-fit">
          <CardContent className="p-3">
            <h2 className="mb-2 px-1 text-xs font-semibold uppercase tracking-wide text-foreground-muted">
              {t('governanceDocs.tree.title')}
            </h2>
            {treeQuery.isLoading ? (
              <div className="flex flex-col gap-2">
                <Skeleton className="h-6 w-full" />
                <Skeleton className="h-6 w-3/4" />
                <Skeleton className="h-6 w-2/3" />
              </div>
            ) : treeQuery.isError ? (
              <p className="px-1 text-sm text-error">{t('governanceDocs.status.error')}</p>
            ) : nodes.length === 0 ? (
              <p className="px-1 text-sm text-foreground-muted">{t('governanceDocs.tree.empty')}</p>
            ) : (
              <DocTree nodes={nodes} selectedPath={selectedPath} onSelect={selectPath} />
            )}
          </CardContent>
        </Card>

        <Card className="min-w-0">
          <CardContent className="flex flex-col gap-3 p-4">
            {!selectedPath ? (
              <p className="py-12 text-center text-sm text-foreground-muted">
                {t('governanceDocs.viewer.empty')}
              </p>
            ) : docQuery.isLoading ? (
              <div className="flex flex-col gap-2">
                <Skeleton className="h-6 w-1/2" />
                <Skeleton className="h-40 w-full" />
              </div>
            ) : docQuery.isError ? (
              <p className="text-sm text-error">{t('governanceDocs.status.loadError')}</p>
            ) : docQuery.data ? (
              <>
                <div className="flex flex-wrap items-center gap-2 border-b border-border pb-3">
                  <div className="min-w-0">
                    <p className="truncate font-mono text-sm font-medium">{docQuery.data.path}</p>
                    <p className="text-xs text-foreground-muted">
                      {t('governanceDocs.viewer.size', { size: docQuery.data.size })}
                    </p>
                  </div>
                  <div className="ml-auto flex items-center gap-2">
                    {dirty && (
                      <span className="text-xs font-medium text-warning">
                        {t('governanceDocs.status.unsaved')}
                      </span>
                    )}
                    {mode === 'view' ? (
                      <>
                        <Button
                          type="button"
                          variant="secondary"
                          size="sm"
                          onClick={() => docQuery.refetch()}
                          aria-label={t('governanceDocs.actions.reload')}
                        >
                          <RotateCw aria-hidden />
                        </Button>
                        <Button
                          type="button"
                          variant="outline"
                          size="sm"
                          onClick={() => setMode('edit')}
                        >
                          <Pencil aria-hidden />
                          {t('governanceDocs.actions.edit')}
                        </Button>
                      </>
                    ) : (
                      <>
                        <Button
                          type="button"
                          variant="ghost"
                          size="sm"
                          onClick={() => {
                            setDraft(docQuery.data!.content);
                            setMode('view');
                          }}
                        >
                          <X aria-hidden />
                          {t('governanceDocs.actions.cancel')}
                        </Button>
                        <Button
                          type="button"
                          size="sm"
                          onClick={handleSave}
                          disabled={saveMutation.isPending || !dirty}
                        >
                          <Save aria-hidden />
                          {saveMutation.isPending
                            ? t('governanceDocs.actions.saving')
                            : t('governanceDocs.actions.save')}
                        </Button>
                      </>
                    )}
                    {confirmingDelete ? (
                      <Button
                        type="button"
                        variant="destructive"
                        size="sm"
                        onClick={handleDelete}
                        disabled={deleteMutation.isPending}
                      >
                        <Trash2 aria-hidden />
                        {t('governanceDocs.actions.confirmDelete')}
                      </Button>
                    ) : (
                      <Button
                        type="button"
                        variant="outline"
                        size="sm"
                        onClick={() => setConfirmingDelete(true)}
                        aria-label={t('governanceDocs.actions.delete')}
                      >
                        <Trash2 aria-hidden />
                      </Button>
                    )}
                  </div>
                </div>

                {confirmingDelete && (
                  <div
                    role="alertdialog"
                    aria-label={t('governanceDocs.confirm.deleteTitle')}
                    className="rounded-md border border-error/40 bg-error/5 p-3 text-sm"
                  >
                    <p className="font-medium">{t('governanceDocs.confirm.deleteTitle')}</p>
                    <p className="mt-1 text-foreground-muted">
                      {t('governanceDocs.confirm.deleteBody', { path: docQuery.data.path })}
                    </p>
                    <div className="mt-2 flex gap-2">
                      <Button
                        type="button"
                        variant="ghost"
                        size="sm"
                        onClick={() => setConfirmingDelete(false)}
                      >
                        {t('governanceDocs.actions.cancel')}
                      </Button>
                    </div>
                  </div>
                )}

                {saveMutation.isError && (
                  <p className="text-sm text-error">{errorMessage(saveMutation.error)}</p>
                )}
                {deleteMutation.isError && (
                  <p className="text-sm text-error">{errorMessage(deleteMutation.error)}</p>
                )}

                {mode === 'edit' ? (
                  <Textarea
                    aria-label={t('governanceDocs.editor.label')}
                    value={draft}
                    onChange={(event) => setDraft(event.target.value)}
                    spellCheck={false}
                    className="min-h-[24rem] font-mono text-xs"
                  />
                ) : isMarkdown(docQuery.data.path) ? (
                  <div className="overflow-x-auto">
                    <MarkdownContent content={docQuery.data.content} />
                  </div>
                ) : (
                  <pre className="overflow-x-auto rounded-md border border-border bg-surface-elevated p-3 font-mono text-xs">
                    {docQuery.data.content}
                  </pre>
                )}
              </>
            ) : null}
          </CardContent>
        </Card>
      </div>
    </div>
  );
}

function errorMessage(error: unknown): string {
  if (error instanceof ApiError) return error.problem.detail ?? error.problem.title;
  return error instanceof Error ? error.message : String(error);
}
