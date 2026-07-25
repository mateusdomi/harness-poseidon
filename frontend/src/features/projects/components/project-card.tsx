import { useTranslation } from 'react-i18next';

import type { Project } from '@/api';
import { Badge } from '@/design-system';
import type { ProjectOperationalSummary } from '@/features/projects/lib/project-operational';
import { formatRelativeTime } from '@/lib/format';
import { priorityVariant, projectStateVariant } from '@/lib/status';

export interface ProjectCardProps {
  project: Project;
  organizationName?: string;
  operational?: ProjectOperationalSummary;
  onSelect: (project: Project) => void;
}

/** Card do projeto: estado, criticidade, organização e última atividade. */
export function ProjectCard({ project, organizationName, operational, onSelect }: ProjectCardProps) {
  const { t } = useTranslation();
  const description =
    project.description.trim().toLowerCase() === 'control plane poseidon.'
      ? t('projects.card.poseidonDescription')
      : project.description;

  return (
    <button
      type="button"
      onClick={() => onSelect(project)}
      className="flex min-h-touch w-full flex-col gap-2 rounded-lg border border-border bg-surface p-4 text-start transition-colors hover:border-border-strong focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
    >
      <span className="flex min-w-0 items-center gap-3">
        <span
          className="flex size-10 shrink-0 items-center justify-center overflow-hidden rounded-md border border-border"
          style={{ backgroundColor: project.brand.primaryColor ?? undefined }}
        >
          {project.brand.logoUrl ? (
            <img
              src={project.brand.logoUrl}
              alt={t('projects.card.logoAlt', { name: project.name })}
              className="size-full object-contain"
            />
          ) : (
            <span className="font-heading text-sm font-semibold">{project.key.slice(0, 2)}</span>
          )}
        </span>
        <span className="min-w-0 truncate font-medium">{project.name}</span>
        <Badge variant="outline">{project.key}</Badge>
      </span>
      <span className="flex flex-wrap items-center gap-2">
        <Badge variant={projectStateVariant(project.state)}>
          {t(`status.projectState.${project.state}`)}
        </Badge>
        <Badge variant={priorityVariant(project.criticality)}>
          {t(`status.priority.${project.criticality}`)}
        </Badge>
        {organizationName ? (
          <span className="text-xs text-foreground-muted">{organizationName}</span>
        ) : null}
      </span>
      <span className="line-clamp-2 text-sm text-foreground-muted">{description}</span>
      <span className="grid gap-2 text-xs sm:grid-cols-2">
        <span>
          <strong>{t('projects.card.phase')}:</strong>{' '}
          {operational?.phaseName ?? t('projects.card.notAvailable')}
        </span>
        <span>
          <strong>{t('projects.card.responsible')}:</strong>{' '}
          {operational?.responsibleName ?? t('projects.card.notAvailable')}
        </span>
        <span>
          <strong>{t('projects.card.workflow')}:</strong>{' '}
          {operational?.workflowName ?? t('projects.card.notAvailable')}
        </span>
        <span>
          <strong>{t('projects.card.health')}:</strong>{' '}
          {t(`projects.card.healthStates.${operational?.health ?? 'unavailable'}`)}
        </span>
        <span className="sm:col-span-2">
          <strong>{t('projects.card.repository')}:</strong>{' '}
          {project.repositoryUrl ?? t('projects.card.notAvailable')} · {project.defaultBranch}
        </span>
      </span>
      {operational?.progressPercent !== null && operational?.progressPercent !== undefined ? (
        <span className="flex flex-col gap-1">
          <span className="flex justify-between gap-2 text-xs text-foreground-muted">
            <span>{t('projects.card.progress')}</span>
            <span>
              {t('projects.card.progressValue', {
                completed: operational.completedTasks,
                total: operational.totalTasks,
                percent: operational.progressPercent,
              })}
            </span>
          </span>
          <span
            role="progressbar"
            aria-label={t('projects.card.progress')}
            aria-valuemin={0}
            aria-valuemax={100}
            aria-valuenow={operational.progressPercent}
            className="h-1.5 overflow-hidden rounded-full bg-surface-elevated"
          >
            <span
              className="block h-full rounded-full bg-brand"
              style={{ width: `${operational.progressPercent}%` }}
            />
          </span>
        </span>
      ) : null}
      {project.technologies.length > 0 ? (
        <span className="flex flex-wrap gap-1">
          {project.technologies.slice(0, 4).map((tech) => (
            <Badge key={tech} variant="default">
              {tech}
            </Badge>
          ))}
        </span>
      ) : null}
      <span className="text-xs text-foreground-muted">
        {t('projects.card.lastActivity', { time: formatRelativeTime(project.lastActivityAt) })}
      </span>
    </button>
  );
}
