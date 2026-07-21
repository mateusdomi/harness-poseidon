import { useEffect, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { Building2 } from 'lucide-react';

import type { Project } from '@/api';
import { Button, Card, CardContent, CardHeader, CardTitle, Skeleton } from '@/design-system';
import { useOrganizations } from '@/features/organizations/hooks/use-organizations';
import { ProjectList } from '@/features/projects/components/project-list';
import {
  ProjectForm,
  type ProjectFormValues,
} from '@/features/projects/components/project-form';
import { BackLink } from '@/features/shared/components/back-link';
import { Breadcrumb } from '@/features/shared/components/breadcrumb';
import {
  useCreateProject,
  useProjectStarted,
  useProjects,
  useUpdateProject,
} from '@/features/projects/hooks/use-projects';

type View = { kind: 'list' } | { kind: 'create' } | { kind: 'edit'; project: Project };

export default function ProjectsPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const projectsQuery = useProjects();
  const organizationsQuery = useOrganizations();
  const createProject = useCreateProject();
  const updateProject = useUpdateProject();
  const [view, setView] = useState<View>({ kind: 'list' });
  const editingProjectId = view.kind === 'edit' ? view.project.id : null;
  const startedQuery = useProjectStarted(editingProjectId);

  // Deep link `?new=1&org=<id>` (golden path / retorno da criação de org).
  const wantsCreate = searchParams.get('new') === '1';
  const preselectedOrgId = searchParams.get('org');
  useEffect(() => {
    if (wantsCreate) {
      setView({ kind: 'create' });
      const next = new URLSearchParams(searchParams);
      next.delete('new');
      setSearchParams(next, { replace: true });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [wantsCreate]);

  function toInput(values: ProjectFormValues) {
    return {
      organizationId: values.organizationId,
      name: values.name,
      key: values.key,
      description: values.description,
      criticality: values.criticality,
      repositoryUrl: values.repositoryUrl === '' ? null : values.repositoryUrl,
      repositoryProvider: values.repositoryProvider,
      defaultBranch: values.defaultBranch,
      technologies: values.technologies,
      brand: values.brand,
      memberProfileIds: values.memberProfileIds,
    };
  }

  async function handleCreate(values: ProjectFormValues) {
    await createProject.mutateAsync(toInput(values));
    setView({ kind: 'list' });
  }

  async function handleUpdate(project: Project, values: ProjectFormValues) {
    await updateProject.mutateAsync({
      id: project.id,
      input: { ...toInput(values), state: values.state },
    });
    setView({ kind: 'list' });
  }

  const organizations = organizationsQuery.data ?? [];
  const loading = projectsQuery.isLoading || organizationsQuery.isLoading;
  const errored = projectsQuery.isError || organizationsQuery.isError;
  const hasOrganizations = organizations.length > 0;
  const breadcrumbBase = { label: t('features.projects.title'), to: '/projects' };

  function retry() {
    void projectsQuery.refetch();
    void organizationsQuery.refetch();
  }

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="font-heading text-2xl font-semibold">{t('features.projects.title')}</h1>
      </div>
      <p className="max-w-prose text-foreground-muted">{t('features.projects.description')}</p>

      {loading ? (
        <div className="flex flex-col gap-3" role="status" aria-label={t('common.states.loading')}>
          <Skeleton className="h-11 w-full" />
          <Skeleton className="h-32 w-full" />
          <Skeleton className="h-32 w-full" />
        </div>
      ) : errored ? (
        <div className="flex flex-col items-start gap-3">
          <p role="alert" className="text-sm text-error">
            {t('common.states.errorBody')}
          </p>
          <Button type="button" variant="outline" onClick={retry}>
            {t('common.actions.retry')}
          </Button>
        </div>
      ) : !hasOrganizations ? (
        // Pré-condição (§6): projeto exige organização. Não abrimos o formulário
        // com um select vazio — orientamos a criar a organização e retornamos ao
        // fluxo de projeto com ela pré-selecionada.
        <Card>
          <CardHeader className="flex flex-row items-center gap-2">
            <Building2 aria-hidden="true" className="size-5 text-foreground-muted" />
            <CardTitle>{t('projects.precondition.title')}</CardTitle>
          </CardHeader>
          <CardContent className="flex flex-col items-start gap-3">
            <p className="max-w-prose text-sm text-foreground-muted">
              {t('projects.precondition.body')}
            </p>
            <Button
              type="button"
              onClick={() => navigate('/organizations?new=1&return=project')}
            >
              {t('projects.precondition.cta')}
            </Button>
          </CardContent>
        </Card>
      ) : view.kind === 'create' ? (
        <div className="flex flex-col gap-3">
          <Breadcrumb items={[breadcrumbBase, { label: t('projects.form.createTitle') }]} />
          <BackLink
            label={t('projects.back')}
            onBack={() => setView({ kind: 'list' })}
            fallbackTo="/projects"
          />
          <ProjectForm
            organizations={organizations}
            defaultOrganizationId={preselectedOrgId ?? undefined}
            submitting={createProject.isPending}
            onSubmit={(values) => void handleCreate(values)}
            onCancel={() => setView({ kind: 'list' })}
          />
        </div>
      ) : view.kind === 'edit' ? (
        <div className="flex flex-col gap-3">
          <Breadcrumb
            items={[breadcrumbBase, { label: view.project.name }, { label: t('projects.form.editTitle') }]}
          />
          <BackLink
            label={t('projects.back')}
            onBack={() => setView({ kind: 'list' })}
            fallbackTo="/projects"
          />
          <ProjectForm
            organizations={organizations}
            initial={view.project}
            started={startedQuery.data ?? false}
            submitting={updateProject.isPending}
            onSubmit={(values) => void handleUpdate(view.project, values)}
            onCancel={() => setView({ kind: 'list' })}
          />
        </div>
      ) : (
        <ProjectList
          projects={projectsQuery.data ?? []}
          organizations={organizations}
          onSelect={(project) => setView({ kind: 'edit', project })}
          onCreateNew={() => setView({ kind: 'create' })}
        />
      )}
    </div>
  );
}
