import { useTranslation } from 'react-i18next';

import type { Project } from '@/api';
import { Badge } from '@/design-system';
import { formatRelativeTime } from '@/lib/format';
import { priorityVariant, projectStateVariant } from '@/lib/status';

export interface ProjectCardProps {
  project: Project;
  organizationName?: string;
  onSelect: (project: Project) => void;
}

/** Card do projeto: estado, criticidade, organização e última atividade. */
export function ProjectCard({ project, organizationName, onSelect }: ProjectCardProps) {
  const { t } = useTranslation();

  return (
    <button
      type="button"
      onClick={() => onSelect(project)}
      className="flex min-h-touch w-full flex-col gap-2 rounded-lg border border-border bg-surface p-4 text-start transition-colors hover:border-border-strong focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
    >
      <span className="flex flex-wrap items-center gap-2">
        <span className="font-medium">{project.name}</span>
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
      <span className="line-clamp-2 text-sm text-foreground-muted">{project.description}</span>
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
