import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useSearchParams } from 'react-router-dom';
import { Upload } from 'lucide-react';

import {
  documentKindSchema,
  documentStateSchema,
  type DocumentKind,
  type DocumentState,
} from '@/api';
import { Button, Card, CardContent, Checkbox, Select, Skeleton } from '@/design-system';
import { DocumentCatalog } from '@/features/documents/components/document-catalog';
import { DocumentDetail } from '@/features/documents/components/document-detail';
import { OrphanDocuments } from '@/features/documents/components/orphan-documents';
import { UploadDocumentDialog } from '@/features/documents/components/upload-document-dialog';
import {
  useDocumentApprovals,
  useDocuments,
  useDocumentsRealtime,
  useWorkflowPhases,
} from '@/features/documents/hooks/use-documents';
import { useActiveProject } from '@/features/shared/hooks/use-active-project';
import { PaginationBar } from '@/features/shared/components/pagination';
import { useUrlPagination } from '@/features/shared/hooks/use-pagination';

/**
 * Catálogo de documentos do projeto ativo: filtros por categoria, fase,
 * estado e flags (inconsistência/waiver); órfãos com classificação;
 * upload externo; deep-link `?doc=<id>` (vindo do chat) abre o detalhe.
 */
export default function UdocumentsPage() {
  const { t } = useTranslation();
  const [searchParams, setSearchParams] = useSearchParams();
  const { projects, activeProject, setActiveProject, isPending, isError, refetch } =
    useActiveProject();
  const projectId = activeProject?.id ?? null;

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

  const openDocumentId = searchParams.get('doc');

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
          <Button type="button" variant="outline" size="sm" onClick={() => setUploadOpen(true)}>
            <Upload aria-hidden="true" />
            {t('documents.upload.open')}
          </Button>
        )}
      </div>

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
            <label className="flex min-h-11 items-center gap-2 text-sm">
              <Checkbox
                checked={onlyInconsistent}
                onChange={(event) => setOnlyInconsistent(event.target.checked)}
              />
              {t('documents.filters.inconsistent')}
            </label>
            <label className="flex min-h-11 items-center gap-2 text-sm">
              <Checkbox
                checked={onlyWaiver}
                onChange={(event) => setOnlyWaiver(event.target.checked)}
              />
              {t('documents.filters.waiver')}
            </label>
          </div>

          {documents.length === 0 ? (
            <Card>
              <CardContent className="flex flex-col items-start gap-3 p-6">
                <h2 className="font-heading text-lg font-semibold">
                  {t('documents.empty.title')}
                </h2>
                <p className="text-sm text-foreground-muted">{t('documents.empty.body')}</p>
              </CardContent>
            </Card>
          ) : (
            <>
              <DocumentCatalog documents={pagination.paginate(documents)} onOpen={openDocument} />
              <PaginationBar pagination={pagination} />
            </>
          )}
        </>
      )}

      {uploadOpen && activeProject && (
        <UploadDocumentDialog projectId={activeProject.id} onClose={() => setUploadOpen(false)} />
      )}
    </div>
  );
}
