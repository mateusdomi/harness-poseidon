import { useTranslation } from 'react-i18next';

import type { Project } from '@/api';
import { usePresentationMode } from '@/app/presentation';
import { Badge } from '@/design-system';
import type { ProjectOperationalSummary } from '@/features/projects/lib/project-operational';
import { V3_LIFECYCLE_MACROS, v3LifecycleMacroIndex } from '@/features/projects/lib/v3-lifecycle';
import { formatRelativeTime } from '@/lib/format';
import { priorityVariant, projectStateVariant } from '@/lib/status';

export interface ProjectCardProps {
  project: Project;
  organizationName?: string;
  operational?: ProjectOperationalSummary;
  onSelect: (project: Project) => void;
}

/** Card do projeto: estado, criticidade, organização e última atividade. */
export function ProjectCard({
  project,
  organizationName,
  operational,
  onSelect,
}: ProjectCardProps) {
  const { t } = useTranslation();
  const { isBusiness } = usePresentationMode();
  const description =
    project.description.trim().toLowerCase() === 'control plane poseidon.'
      ? t('projects.card.poseidonDescription')
      : project.description;

  return (
    <button
      type="button"
      onClick={() => onSelect(project)}
      className="flex min-h-touch w-full min-w-0 max-w-full flex-col gap-2 overflow-hidden rounded-lg border border-border bg-surface p-4 text-start transition-colors hover:border-border-strong focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
    >
      <span className="flex w-full min-w-0 items-center gap-3">
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
        <Badge variant="outline" className="max-w-24 shrink-0 truncate">
          {project.key}
        </Badge>
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
      <span className="grid gap-2 text-xs md:grid-cols-2">
        <span>
          <strong>{t('projects.card.lifecycle')}:</strong>{' '}
          {operational?.lifecycleLabel ?? t('projects.card.lifecycleStates.understand')}
          {operational?.lifecycleStatus ? ` · ${operational.lifecycleStatus}` : ''}
        </span>
        <span>
          <strong>{t('projects.card.responsible')}:</strong>{' '}
          {operational?.responsibleName ?? t('projects.card.notAvailable')}
        </span>
        <span>
          <strong>{t('projects.card.running')}:</strong>{' '}
          {t('projects.card.runningValue', { count: operational?.runningExecutions ?? 0 })}
        </span>
        <span>
          <strong>{t('projects.card.health')}:</strong>{' '}
          {t(`projects.card.healthStates.${operational?.health ?? 'unavailable'}`)}
        </span>
        {!isBusiness && (
          <span className="min-w-0 break-words md:col-span-2">
            <strong>{t('projects.card.repository')}:</strong>{' '}
            {project.repositoryUrl ?? t('projects.card.notAvailable')} · {project.defaultBranch}
          </span>
        )}
      </span>
      <LifecycleSegments current={operational?.lifecycleState ?? 'understand'} />
      {!isBusiness && project.technologies.length > 0 ? (
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

function LifecycleSegments({
  current,
}: {
  current: NonNullable<ProjectOperationalSummary['lifecycleState']>;
}) {
  const currentIndex = v3LifecycleMacroIndex(current);
  return (
    <span className="grid grid-cols-2 gap-1 text-[11px] md:grid-cols-4">
      {V3_LIFECYCLE_MACROS.map((state, index) => {
        const done = index < currentIndex;
        const active = index === currentIndex;
        return (
          <span
            key={state.id}
            className={
              done
                ? 'rounded bg-success/15 px-2 py-1 text-success'
                : active
                  ? 'rounded bg-brand/15 px-2 py-1 text-brand-strong'
                  : 'rounded bg-surface-elevated px-2 py-1 text-foreground-muted'
            }
          >
            {done ? '✓ ' : active ? '● ' : '○ '}
            {state.label}
          </span>
        );
      })}
    </span>
  );
}
