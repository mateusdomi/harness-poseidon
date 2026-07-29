import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { FileArchive, ImageIcon, Pencil, Upload } from 'lucide-react';

import {
  Badge,
  Button,
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  Select,
  Skeleton,
} from '@/design-system';
import { prototypeStateVariant } from '@/lib/status';
import { formatDate } from '@/lib/format';
import { PrototypingModeDialog } from '@/features/prototypes/components/prototyping-mode-dialog';
import { ReferenceImage } from '@/features/prototypes/components/reference-image';
import { UploadReferenceDialog } from '@/features/prototypes/components/upload-reference-dialog';
import {
  useOrganizations,
  usePrototypes,
  usePrototypingStage,
  usePrototypesRealtime,
  useVisualReferences,
} from '@/features/prototypes/hooks/use-prototypes';
import {
  BRIEFING_TAG_PREFIX,
  FLOW_MOMENT_TAG_PREFIX,
  effectiveBrand,
  isZipReference,
  plainTags,
  prefixedTagValue,
} from '@/features/prototypes/lib/prototypes-derive';
import { useActiveProject } from '@/features/shared/hooks/use-active-project';
import { PaginationBar } from '@/features/shared/components/pagination';
import { usePagination } from '@/features/shared/hooks/use-pagination';

/**
 * Prototipação e referências visuais: cenário por projeto (protótipo
 * externo, apenas diretrizes, geração autônoma ou não aplicável com waiver),
 * galeria de protótipos e referências, metadados de marca e upload
 * (imagens e ZIP somente como referência — nunca executados).
 */
export default function UprototypesPage() {
  const { t } = useTranslation();
  const { projects, activeProject, setActiveProject, isPending, isError, refetch } =
    useActiveProject();
  const projectId = activeProject?.id ?? null;

  const prototypesQuery = usePrototypes(projectId);
  const referencesQuery = useVisualReferences(projectId);
  const stageQuery = usePrototypingStage(projectId);
  const organizationsQuery = useOrganizations();
  usePrototypesRealtime(projectId);

  const [modeDialogOpen, setModeDialogOpen] = useState(false);
  const [uploadOpen, setUploadOpen] = useState(false);

  const organization =
    organizationsQuery.data?.find((org) => org.id === activeProject?.organizationId) ?? null;
  const brand = activeProject ? effectiveBrand(activeProject, organization) : null;
  const notApplicable = activeProject?.prototyping.mode === 'notApplicable';

  const loading =
    isPending ||
    prototypesQuery.isLoading ||
    referencesQuery.isLoading ||
    stageQuery.isLoading ||
    organizationsQuery.isLoading;
  const errored =
    isError || prototypesQuery.isError || referencesQuery.isError || stageQuery.isError;

  // Galerias paginadas independentemente; reset ao trocar de projeto.
  const prototypesPagination = usePagination((prototypesQuery.data ?? []).length, {
    resetKey: projectId,
  });
  const referencesPagination = usePagination((referencesQuery.data ?? []).length, {
    resetKey: projectId,
  });

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="font-heading text-2xl font-semibold">{t('features.prototypes.title')}</h1>
        {projects.length > 0 && (
          <div className="ml-auto flex items-center gap-2">
            <label htmlFor="prototypes-project" className="text-sm text-foreground-muted">
              {t('cockpit.projectSelector.label')}
            </label>
            <Select
              id="prototypes-project"
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
        {activeProject && !notApplicable && (
          <Button type="button" variant="outline" size="sm" onClick={() => setUploadOpen(true)}>
            <Upload aria-hidden="true" />
            {t('prototypes.upload.open')}
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
              void prototypesQuery.refetch();
              void referencesQuery.refetch();
              void stageQuery.refetch();
            }}
          >
            {t('common.actions.retry')}
          </Button>
        </div>
      ) : !activeProject ? (
        <Card>
          <CardContent className="flex flex-col items-start gap-3 p-6">
            <p className="text-sm text-foreground-muted">{t('prototypes.noProject.body')}</p>
          </CardContent>
        </Card>
      ) : (
        <>
          <Card>
            <CardHeader className="flex-row flex-wrap items-center gap-3">
              <CardTitle>{t('prototypes.mode.current')}</CardTitle>
              <Badge variant="brand">
                {t(`status.prototypingMode.${activeProject.prototyping.mode}`)}
              </Badge>
              <Button
                type="button"
                variant="ghost"
                size="sm"
                onClick={() => setModeDialogOpen(true)}
              >
                <Pencil aria-hidden="true" />
                {t('prototypes.mode.change')}
              </Button>
            </CardHeader>
            <CardContent className="flex flex-col gap-3">
              {activeProject.prototyping.waiver && (
                <p className="text-sm text-foreground-muted">
                  {t('prototypes.mode.waiverInfo', {
                    reason: activeProject.prototyping.waiver.reason,
                    date: formatDate(activeProject.prototyping.waiver.grantedAt),
                  })}
                </p>
              )}
              {brand && (
                <div className="flex flex-wrap items-center gap-4 text-sm">
                  {brand.brand.logoUrl ? (
                    <img
                      src={brand.brand.logoUrl}
                      alt={t('prototypes.brand.logoAlt')}
                      className="h-8 w-auto rounded"
                    />
                  ) : (
                    <span className="text-foreground-muted">{t('prototypes.brand.noLogo')}</span>
                  )}
                  {(['primaryColor', 'secondaryColor'] as const).map((field) => (
                    <span key={field} className="flex items-center gap-2">
                      <span
                        aria-hidden="true"
                        className="size-5 rounded border border-border"
                        style={
                          brand.brand[field] ? { backgroundColor: brand.brand[field] } : undefined
                        }
                      />
                      <span className="text-foreground-muted">
                        {t(`common.brand.fields.${field}`)}:{' '}
                        {brand.brand[field] ?? t('common.brand.emptyPlaceholder')}
                      </span>
                    </span>
                  ))}
                  {brand.inheritedFields.length > 0 && (
                    <span className="text-xs text-foreground-muted">
                      {t('common.inheritance.inheritedFromOrg')}
                    </span>
                  )}
                </div>
              )}
            </CardContent>
          </Card>

          {stageQuery.data && (
            <Card>
              <CardHeader className="flex-row flex-wrap items-center gap-3">
                <CardTitle>{t('prototypes.stage.title')}</CardTitle>
                <Badge variant={stageQuery.data.blocksAdvance ? 'warning' : 'success'}>
                  {t(`prototypes.stage.states.${stageQuery.data.state}`)}
                </Badge>
              </CardHeader>
              <CardContent className="flex flex-col gap-3">
                <p className="text-sm">{stageQuery.data.businessMessage}</p>
                <p className="text-xs text-foreground-muted">
                  {t('prototypes.stage.entryPath')}:{' '}
                  {t(`prototypes.stage.paths.${stageQuery.data.entryPath}`)}
                </p>
                {stageQuery.data.inherited.length > 0 && (
                  <div>
                    <p className="text-sm font-medium">{t('prototypes.stage.inherited')}</p>
                    <ul className="list-disc pl-5 text-sm text-foreground-muted">
                      {stageQuery.data.inherited.map((item) => (
                        <li key={item}>{item}</li>
                      ))}
                    </ul>
                  </div>
                )}
                {stageQuery.data.questions.length > 0 && (
                  <div>
                    <p className="text-sm font-medium">{t('prototypes.stage.missing')}</p>
                    <ul className="list-disc pl-5 text-sm text-foreground-muted">
                      {stageQuery.data.questions.map((item) => (
                        <li key={item}>{item}</li>
                      ))}
                    </ul>
                  </div>
                )}
              </CardContent>
            </Card>
          )}

          {notApplicable ? (
            <Card>
              <CardContent className="flex flex-col items-start gap-3 p-6">
                <h2 className="font-heading text-lg font-semibold">
                  {t('prototypes.notApplicable.title')}
                </h2>
                <p className="text-sm text-foreground-muted">
                  {t('prototypes.notApplicable.body')}
                </p>
              </CardContent>
            </Card>
          ) : (
            <>
              <section className="flex flex-col gap-3" aria-labelledby="prototypes-gallery">
                <h2 id="prototypes-gallery" className="font-heading text-lg font-semibold">
                  {t('prototypes.sections.prototypes')}
                </h2>
                {(prototypesQuery.data ?? []).length === 0 ? (
                  <p className="text-sm text-foreground-muted">
                    {t('prototypes.empty.prototypes')}
                  </p>
                ) : (
                  <>
                    <ul className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
                      {prototypesPagination
                        .paginate(prototypesQuery.data ?? [])
                        .map((prototype) => (
                          <li key={prototype.id}>
                            <Card className="h-full">
                              <CardContent className="flex h-full flex-col gap-2 p-4">
                                {prototype.thumbnailUrl ? (
                                  <img
                                    src={prototype.thumbnailUrl}
                                    alt=""
                                    className="h-28 w-full rounded object-cover"
                                  />
                                ) : (
                                  <div className="flex h-28 items-center justify-center rounded border border-border bg-surface-elevated">
                                    <ImageIcon
                                      aria-hidden="true"
                                      className="size-8 text-foreground-muted"
                                    />
                                  </div>
                                )}
                                <div className="flex items-center gap-2">
                                  <span className="font-medium">{prototype.name}</span>
                                  <Badge variant={prototypeStateVariant(prototype.state)}>
                                    {t(`status.prototypeState.${prototype.state}`)}
                                  </Badge>
                                </div>
                                <p className="text-sm text-foreground-muted">
                                  {prototype.description}
                                </p>
                                {prototype.url && (
                                  <a
                                    href={prototype.url}
                                    target="_blank"
                                    rel="noreferrer"
                                    className="mt-auto inline-flex min-h-11 items-center text-sm text-brand-strong underline-offset-4 hover:underline"
                                  >
                                    {t('prototypes.openExternal')}
                                  </a>
                                )}
                              </CardContent>
                            </Card>
                          </li>
                        ))}
                    </ul>
                    <PaginationBar pagination={prototypesPagination} />
                  </>
                )}
                <p className="text-xs text-foreground-muted">
                  {t('prototypes.versions.unavailable')}
                </p>
              </section>

              <section className="flex flex-col gap-3" aria-labelledby="references-gallery">
                <h2 id="references-gallery" className="font-heading text-lg font-semibold">
                  {t('prototypes.sections.references')}
                </h2>
                {(referencesQuery.data ?? []).length === 0 ? (
                  <p className="text-sm text-foreground-muted">
                    {t('prototypes.empty.references')}
                  </p>
                ) : (
                  <>
                    <ul className="grid gap-3 md:grid-cols-2 xl:grid-cols-4">
                      {referencesPagination
                        .paginate(referencesQuery.data ?? [])
                        .map((reference) => {
                          const briefing = prefixedTagValue(reference.tags, BRIEFING_TAG_PREFIX);
                          const flowMoment = prefixedTagValue(
                            reference.tags,
                            FLOW_MOMENT_TAG_PREFIX,
                          );
                          return (
                            <li key={reference.id}>
                              <Card className="h-full">
                                <CardContent className="flex h-full flex-col gap-2 p-4">
                                  {isZipReference(reference) ? (
                                    <div className="flex h-24 items-center justify-center gap-2 rounded border border-border bg-surface-elevated text-foreground-muted">
                                      <FileArchive aria-hidden="true" className="size-6" />
                                      <span className="text-xs">{t('prototypes.zipBadge')}</span>
                                    </div>
                                  ) : (
                                    <ReferenceImage
                                      src={reference.imageUrl}
                                      alt={reference.title}
                                      className="h-24 w-full rounded object-cover"
                                    />
                                  )}
                                  <div className="flex flex-wrap items-center gap-2">
                                    <span className="font-medium">{reference.title}</span>
                                    <Badge variant="outline">
                                      {t(`status.visualReferenceSource.${reference.source}`)}
                                    </Badge>
                                  </div>
                                  {briefing && (
                                    <p className="text-xs text-foreground-muted">
                                      {t('prototypes.metadata.briefing')}: {briefing}
                                    </p>
                                  )}
                                  {flowMoment && (
                                    <p className="text-xs text-foreground-muted">
                                      {t('prototypes.metadata.flowMoment')}: {flowMoment}
                                    </p>
                                  )}
                                  {plainTags(reference.tags).length > 0 && (
                                    <div className="mt-auto flex flex-wrap gap-1">
                                      {plainTags(reference.tags).map((tag) => (
                                        <Badge key={tag} variant="outline">
                                          {tag}
                                        </Badge>
                                      ))}
                                    </div>
                                  )}
                                </CardContent>
                              </Card>
                            </li>
                          );
                        })}
                    </ul>
                    <PaginationBar pagination={referencesPagination} />
                  </>
                )}
              </section>
            </>
          )}
        </>
      )}

      {modeDialogOpen && activeProject && (
        <PrototypingModeDialog project={activeProject} onClose={() => setModeDialogOpen(false)} />
      )}
      {uploadOpen && projectId && (
        <UploadReferenceDialog projectId={projectId} onClose={() => setUploadOpen(false)} />
      )}
    </div>
  );
}
