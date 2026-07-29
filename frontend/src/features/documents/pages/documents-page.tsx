import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useSearchParams } from 'react-router-dom';
import { Download, FilePlus2, FileText, Upload } from 'lucide-react';

import {
  documentKindSchema,
  documentStateSchema,
  type DocumentKind,
  type DocumentState,
} from '@/api';
import { usePresentationMode } from '@/app/presentation';
import { Button, Card, CardContent, Checkbox, Select, Skeleton } from '@/design-system';
import { ApprovalInbox } from '@/features/documents/components/approval-inbox';
import { CreateDocumentDialog } from '@/features/documents/components/create-document-dialog';
import { DocumentCatalog } from '@/features/documents/components/document-catalog';
import { DocumentDetail } from '@/features/documents/components/document-detail';
import { DocumentsTabs } from '@/features/documents/components/documents-tabs';
import { OrphanDocuments } from '@/features/documents/components/orphan-documents';
import { UploadDocumentDialog } from '@/features/documents/components/upload-document-dialog';
import {
  useDocumentApprovals,
  useDocuments,
  useDocumentsRealtime,
  useWorkflowPhases,
} from '@/features/documents/hooks/use-documents';
import {
  documentPanelId,
  documentTabId,
  parseDocumentTab,
  type DocumentTabId,
} from '@/features/documents/lib/documents-tabs';
import { downloadProjectDocuments } from '@/features/delivery/lib/download-documents';
import { useActiveProject } from '@/features/shared/hooks/use-active-project';
import { PaginationBar } from '@/features/shared/components/pagination';
import { useUrlPagination } from '@/features/shared/hooks/use-pagination';

/**
 * A ÚNICA tela de documentos do modo Negócio (D9). Eram quatro — Documentos,
 * Aprovações, Governança e Documentos de Governança — e o dono não tinha como
 * saber qual era qual. Aqui ficam os artefatos que a EQUIPE produziu para o
 * projeto, com duas abas: o catálogo e o que aguarda decisão dele. As duas de
 * governança tratam dos arquivos internos do sistema e vivem no modo Técnico.
 *
 * Detalhe de um documento por deep-link `?doc=<id>` (vindo do chat); aba por
 * `?tab=` (é para cá que o endereço antigo `/approvals` redireciona).
 */
export default function UdocumentsPage() {
  const { t } = useTranslation();
  const [searchParams, setSearchParams] = useSearchParams();
  const { projects, activeProject, setActiveProject, isPending, isError, refetch } =
    useActiveProject();
  const projectId = activeProject?.id ?? null;
  const { showTechnicalDetails } = usePresentationMode();

  const documentsQuery = useDocuments(projectId);
  const approvalsQuery = useDocumentApprovals(projectId);
  const { phases } = useWorkflowPhases(projectId);
  useDocumentsRealtime(projectId);

  const [kindFilter, setKindFilter] = useState<DocumentKind | ''>('');
  const [phaseFilter, setPhaseFilter] = useState('');
  const [stateFilter, setStateFilter] = useState<DocumentState | ''>('');
  const [onlyInconsistent, setOnlyInconsistent] = useState(false);
  const [onlyWaiver, setOnlyWaiver] = useState(false);
  const [uploadOpen, setUploadOpen] = useState(false);
  const [createOpen, setCreateOpen] = useState(false);
  const [downloading, setDownloading] = useState(false);
  const [downloadFailed, setDownloadFailed] = useState(false);

  function resetFilters() {
    setKindFilter('');
    setPhaseFilter('');
    setStateFilter('');
    setOnlyInconsistent(false);
    setOnlyWaiver(false);
  }

  const openDocumentId = searchParams.get('doc');
  const activeTab = parseDocumentTab(searchParams.get('tab'));

  function openDocument(id: string) {
    setSearchParams(
      (previous) => {
        const next = new URLSearchParams(previous);
        next.set('doc', id);
        return next;
      },
      { preventScrollReset: true },
    );
  }

  function closeDocument() {
    setSearchParams(
      (previous) => {
        const next = new URLSearchParams(previous);
        next.delete('doc');
        return next;
      },
      { preventScrollReset: true },
    );
  }

  function selectTab(next: DocumentTabId) {
    setSearchParams(
      (previous) => {
        const params = new URLSearchParams(previous);
        // O catálogo é o padrão: não sujar a URL com o valor default.
        if (next === 'catalog') params.delete('tab');
        else params.set('tab', next);
        params.delete('page');
        return params;
      },
      { preventScrollReset: true },
    );
  }

  async function downloadPackage(id: string) {
    setDownloading(true);
    setDownloadFailed(false);
    try {
      await downloadProjectDocuments(id);
    } catch {
      // Clique sem resposta é o pior resultado possível: se o pacote não veio,
      // a tela diz isso em linguagem do dono.
      setDownloadFailed(true);
    } finally {
      setDownloading(false);
    }
  }

  const loading = isPending || documentsQuery.isLoading;
  const errored = isError || documentsQuery.isError;
  const documents = useMemo(() => {
    let result = documentsQuery.data ?? [];
    if (kindFilter !== '') result = result.filter((doc) => doc.kind === kindFilter);
    if (phaseFilter !== '') result = result.filter((doc) => doc.phaseName === phaseFilter);
    if (stateFilter !== '') result = result.filter((doc) => doc.state === stateFilter);
    if (onlyInconsistent) result = result.filter((doc) => doc.inconsistent);
    if (onlyWaiver) result = result.filter((doc) => doc.waiver !== null);
    return result;
  }, [documentsQuery.data, kindFilter, phaseFilter, stateFilter, onlyInconsistent, onlyWaiver]);

  const orphans = (documentsQuery.data ?? []).filter((doc) => doc.phaseName === null);
  // Distingue "projeto sem nenhum documento" (empty-state orientado) de
  // "os filtros esconderam tudo" (empty-state de filtros com reset).
  const hasAnyDocuments = (documentsQuery.data ?? []).length > 0;
  const pendingApprovals = (approvalsQuery.data ?? []).filter(
    (approval) => approval.state === 'pending',
  ).length;

  // Paginação client-side com estado na URL (?page=/?pageSize=), preservando
  // o deep-link ?doc=. Reset para a página 1 ao mudar filtros/projeto.
  const pagination = useUrlPagination(documents.length, {
    resetKey: `${projectId}:${kindFilter}:${phaseFilter}:${stateFilter}:${onlyInconsistent}:${onlyWaiver}`,
  });

  // Deep-link: detalhe substitui o catálogo (mobile e desktop).
  if (openDocumentId && !loading && !errored && activeProject) {
    return (
      <DocumentDetail
        documentId={openDocumentId}
        approvals={approvalsQuery.data ?? []}
        chiefAgentId={activeProject.chiefAgentId}
        onBack={closeDocument}
      />
    );
  }

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="font-heading text-2xl font-semibold">{t('features.documents.title')}</h1>
        {projects.length > 0 && (
          <div className="ml-auto flex items-center gap-2">
            <label htmlFor="documents-project" className="text-sm text-foreground-muted">
              {t('cockpit.projectSelector.label')}
            </label>
            <Select
              id="documents-project"
              className="w-auto min-w-48"
              value={activeProject?.id ?? ''}
              onChange={(event) => setActiveProject(event.target.value)}
            >
              {projects.map((project) => (
                <option key={project.id} value={project.id}>
                  {project.name}
                </option>
              ))}
            </Select>
          </div>
        )}
        {activeProject && (
          <div className="flex flex-wrap items-center gap-2">
            <Button type="button" size="sm" onClick={() => setCreateOpen(true)}>
              <FilePlus2 aria-hidden="true" />
              {t('documents.create.open')}
            </Button>
            <Button type="button" variant="outline" size="sm" onClick={() => setUploadOpen(true)}>
              <Upload aria-hidden="true" />
              {t('documents.upload.open')}
            </Button>
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={downloading}
              onClick={() => void downloadPackage(activeProject.id)}
            >
              <Download aria-hidden="true" />
              {downloading ? t('documents.export.pending') : t('documents.export.open')}
            </Button>
          </div>
        )}
      </div>

      <p className="max-w-3xl text-sm text-foreground-muted">{t('documents.intro')}</p>
      {downloadFailed && (
        <p role="alert" className="text-sm text-error">
          {t('documents.export.failed')}
        </p>
      )}

      {loading ? (
        <div className="flex flex-col gap-3" role="status" aria-label={t('common.states.loading')}>
          <Skeleton className="h-10 w-full" />
          <Skeleton className="h-64 w-full" />
        </div>
      ) : errored ? (
        <div className="flex flex-col items-start gap-3">
          <p role="alert" className="text-sm text-error">
            {t('common.states.errorBody')}
          </p>
          <Button
            type="button"
            variant="outline"
            onClick={() => {
              refetch();
              void documentsQuery.refetch();
            }}
          >
            {t('common.actions.retry')}
          </Button>
        </div>
      ) : !activeProject ? (
        <Card>
          <CardContent className="flex flex-col items-start gap-3 p-6">
            <p className="text-sm text-foreground-muted">{t('documents.noProject.body')}</p>
            <Button asChild>
              <Link to="/projects">{t('documents.noProject.cta')}</Link>
            </Button>
          </CardContent>
        </Card>
      ) : (
        <>
          <DocumentsTabs
            activeId={activeTab}
            onChange={selectTab}
            tabs={[
              {
                id: 'catalog',
                label: t('documents.tabs.catalog'),
                count: (documentsQuery.data ?? []).length,
              },
              {
                id: 'approvals',
                label: t('documents.tabs.approvals'),
                count: pendingApprovals,
              },
            ]}
          />

          <div
            role="tabpanel"
            id={documentPanelId(activeTab)}
            aria-labelledby={documentTabId(activeTab)}
            tabIndex={-1}
            className="flex flex-col gap-4"
          >
            {activeTab === 'approvals' ? (
              <ApprovalInbox projectId={activeProject.id} />
            ) : (
              <>
                <OrphanDocuments orphans={orphans} phases={phases} />

                <div className="flex flex-wrap items-end gap-3">
                  <div className="flex flex-col gap-1">
                    <label htmlFor="filter-kind" className="text-xs font-medium">
                      {t('documents.filters.kind')}
                    </label>
                    <Select
                      id="filter-kind"
                      value={kindFilter}
                      onChange={(event) => setKindFilter(event.target.value as DocumentKind | '')}
                    >
                      <option value="">{t('documents.filters.all')}</option>
                      {documentKindSchema.options.map((kind) => (
                        <option key={kind} value={kind}>
                          {t(`status.documentKind.${kind}`)}
                        </option>
                      ))}
                    </Select>
                  </div>
                  <div className="flex flex-col gap-1">
                    <label htmlFor="filter-phase" className="text-xs font-medium">
                      {t('documents.filters.phase')}
                    </label>
                    <Select
                      id="filter-phase"
                      value={phaseFilter}
                      onChange={(event) => setPhaseFilter(event.target.value)}
                    >
                      <option value="">{t('documents.filters.all')}</option>
                      {phases.map((phase) => (
                        <option key={phase} value={phase}>
                          {phase}
                        </option>
                      ))}
                    </Select>
                  </div>
                  <div className="flex flex-col gap-1">
                    <label htmlFor="filter-state" className="text-xs font-medium">
                      {t('documents.filters.state')}
                    </label>
                    <Select
                      id="filter-state"
                      value={stateFilter}
                      onChange={(event) => setStateFilter(event.target.value as DocumentState | '')}
                    >
                      <option value="">{t('documents.filters.all')}</option>
                      {documentStateSchema.options.map((state) => (
                        <option key={state} value={state}>
                          {t(`status.documentState.${state}`)}
                        </option>
                      ))}
                    </Select>
                  </div>
                  {/* Inconsistência e dispensa de obrigação são leitura de
                      auditoria: no modo Negócio não existem, nem como filtro. */}
                  {showTechnicalDetails && (
                    <>
                      <label className="flex min-h-11 items-center gap-2 text-sm">
                        <Checkbox
                          checked={onlyInconsistent}
                          onChange={(event) => setOnlyInconsistent(event.target.checked)}
                        />
                        {t('documents.filters.inconsistentTechnical')}
                      </label>
                      <label className="flex min-h-11 items-center gap-2 text-sm">
                        <Checkbox
                          checked={onlyWaiver}
                          onChange={(event) => setOnlyWaiver(event.target.checked)}
                        />
                        {t('documents.filters.waiverTechnical')}
                      </label>
                    </>
                  )}
                </div>

                {documents.length === 0 ? (
                  hasAnyDocuments ? (
                    <Card>
                      <CardContent className="flex flex-col items-start gap-3 p-6">
                        <h2 className="font-heading text-lg font-semibold">
                          {t('documents.emptyFiltered.title')}
                        </h2>
                        <p className="text-sm text-foreground-muted">
                          {t('documents.emptyFiltered.body')}
                        </p>
                        <Button type="button" variant="outline" size="sm" onClick={resetFilters}>
                          {t('documents.emptyFiltered.reset')}
                        </Button>
                      </CardContent>
                    </Card>
                  ) : (
                    <Card>
                      <CardContent className="flex flex-col items-start gap-4 p-6">
                        <div className="flex items-center gap-3">
                          <span className="flex size-10 shrink-0 items-center justify-center rounded-full bg-surface-elevated text-accent">
                            <FileText aria-hidden="true" className="size-5" />
                          </span>
                          <h2 className="font-heading text-lg font-semibold">
                            {t('documents.empty.title')}
                          </h2>
                        </div>
                        <p className="text-sm text-foreground-muted">{t('documents.empty.body')}</p>
                        <p className="text-sm text-foreground-muted">
                          {t('documents.empty.appears')}
                        </p>
                        <div className="flex flex-wrap gap-2">
                          <Button type="button" onClick={() => setCreateOpen(true)}>
                            <FilePlus2 aria-hidden="true" />
                            {t('documents.empty.createCta')}
                          </Button>
                          <Button type="button" variant="outline" onClick={() => setUploadOpen(true)}>
                            <Upload aria-hidden="true" />
                            {t('documents.empty.uploadCta')}
                          </Button>
                        </div>
                        {/* Os arquivos internos do sistema são assunto técnico:
                            o atalho só existe para quem tem essa tela. */}
                        {showTechnicalDetails && (
                          <div className="mt-1 flex flex-col items-start gap-1 rounded-lg border border-border bg-surface p-3">
                            <p className="text-xs text-foreground-muted">
                              {t('documents.empty.governanceNote')}
                            </p>
                            <Button asChild variant="ghost" size="sm">
                              <Link to="/governance-docs">
                                {t('documents.empty.governanceLink')}
                              </Link>
                            </Button>
                          </div>
                        )}
                      </CardContent>
                    </Card>
                  )
                ) : (
                  <>
                    <DocumentCatalog
                      documents={pagination.paginate(documents)}
                      onOpen={openDocument}
                    />
                    <PaginationBar pagination={pagination} />
                  </>
                )}
              </>
            )}
          </div>
        </>
      )}

      {createOpen && activeProject && (
        <CreateDocumentDialog
          projectId={activeProject.id}
          onClose={() => setCreateOpen(false)}
          onCreated={(id) => {
            setCreateOpen(false);
            openDocument(id);
          }}
        />
      )}

      {uploadOpen && activeProject && (
        <UploadDocumentDialog projectId={activeProject.id} onClose={() => setUploadOpen(false)} />
      )}
    </div>
  );
}
