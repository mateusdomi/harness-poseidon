import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import type { Project } from '@/api';
import { Button, Skeleton } from '@/design-system';
import { useOrganizations } from '@/features/organizations/hooks/use-organizations';
import { ProjectList } from '@/features/projects/components/project-list';
import {
  ProjectForm,
  type ProjectFormValues,
} from '@/features/projects/components/project-form';
import {
  useCreateProject,
  useProjects,
  useUpdateProject,
} from '@/features/projects/hooks/use-projects';

type View = { kind: 'list' } | { kind: 'create' } | { kind: 'edit'; project: Project };

export default function ProjectsPage() {
  const { t } = useTranslation();
  const projectsQuery = useProjects();
  const organizationsQuery = useOrganizations();
  const createProject = useCreateProject();
  const updateProject = useUpdateProject();
  const [view, setView] = useState<View>({ kind: 'list' });

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
  const loading = projectsQuery.isPending || organizationsQuery.isPending;

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="font-heading text-2xl font-semibold">{t('features.projects.title')}</h1>
      </div>
      <p className="max-w-prose text-foreground-muted">{t('features.projects.description')}</p>

      {view.kind === 'create' && (
        <ProjectForm
          organizations={organizations}
          submitting={createProject.isPending}
          onSubmit={(values) => void handleCreate(values)}
          onCancel={() => setView({ kind: 'list' })}
        />
      )}

      {view.kind === 'edit' && (
        <ProjectForm
          organizations={organizations}
          initial={view.project}
          submitting={updateProject.isPending}
          onSubmit={(values) => void handleUpdate(view.project, values)}
          onCancel={() => setView({ kind: 'list' })}
        />
      )}

      {view.kind === 'list' && (
        <>
          {loading ? (
            <div className="flex flex-col gap-3" role="status" aria-label={t('common.states.loading')}>
              <Skeleton className="h-11 w-full" />
              <Skeleton className="h-32 w-full" />
              <Skeleton className="h-32 w-full" />
            </div>
          ) : projectsQuery.isError || organizationsQuery.isError ? (
            <div className="flex flex-col items-start gap-3">
              <p role="alert" className="text-sm text-error">
                {t('common.states.errorBody')}
              </p>
              <Button
                type="button"
                variant="outline"
                onClick={() => {
                  void projectsQuery.refetch();
                  void organizationsQuery.refetch();
                }}
              >
                {t('common.actions.retry')}
              </Button>
            </div>
          ) : (
            <ProjectList
              projects={projectsQuery.data}
              organizations={organizations}
              onSelect={(project) => setView({ kind: 'edit', project })}
              onCreateNew={() => setView({ kind: 'create' })}
            />
          )}
        </>
      )}
    </div>
  );
}
