import { useTranslation } from 'react-i18next';
import { AlertTriangle, RefreshCw } from 'lucide-react';

import { Button, Select, Skeleton } from '@/design-system';
import { useCockpitAgents, useCockpitWorkflow } from '@/features/cockpit/hooks/use-cockpit';
import { useActiveProject } from '@/features/shared/hooks/use-active-project';

/**
 * Contexto real do header (desktop lg+): projeto ativo, fase atual e agentes
 * trabalhando. Reutiliza os hooks/queries existentes (mesmas query keys do
 * cockpit — cache compartilhado, nenhum endpoint novo). Se o projeto ainda
 * não estiver disponível, renderiza nada (sem placeholder).
 */
export function HeaderContext() {
  const { t } = useTranslation();
  const {
    projects,
    activeProject,
    setActiveProject,
    selectionUnavailable,
    isPending,
    isError,
    refetch,
  } = useActiveProject();
  const projectId = activeProject?.id ?? null;
  const { phases } = useCockpitWorkflow(projectId);
  const agentsQuery = useCockpitAgents(projectId);

  const activePhase = phases.find((phase) => phase.state === 'active') ?? null;
  const workingCount = (agentsQuery.data ?? []).filter((agent) => agent.state === 'working').length;

  if (isPending) {
    return (
      <div
        role="status"
        aria-label={t('shell.project.loading')}
        className="min-w-0 flex-1 md:max-w-64"
      >
        <Skeleton className="h-9 w-full" />
      </div>
    );
  }

  if (isError) {
    return (
      <Button
        type="button"
        variant="outline"
        size="sm"
        className="min-w-0 max-w-48 flex-1 justify-start text-error"
        onClick={refetch}
      >
        <RefreshCw aria-hidden="true" className="size-4 shrink-0" />
        <span className="truncate">{t('shell.project.error')}</span>
      </Button>
    );
  }

  return (
    <div className="flex min-w-0 flex-1 items-center gap-2 md:max-w-xl">
      <label htmlFor="global-project-selector" className="sr-only">
        {t('shell.project.label')}
      </label>
      <Select
        id="global-project-selector"
        aria-invalid={selectionUnavailable || undefined}
        aria-describedby={selectionUnavailable ? 'global-project-unavailable' : undefined}
        className="min-w-0 flex-1 truncate md:max-w-64"
        value={activeProject?.id ?? ''}
        disabled={projects.length === 0}
        onChange={(event) => setActiveProject(event.target.value)}
      >
        {(selectionUnavailable || projects.length === 0) && (
          <option value="">
            {selectionUnavailable
              ? t('shell.project.unavailable')
              : t('shell.project.empty')}
          </option>
        )}
        {projects.map((project) => (
          <option key={project.id} value={project.id}>
            {project.name}
          </option>
        ))}
      </Select>
      {selectionUnavailable && (
        <span
          id="global-project-unavailable"
          role="alert"
          className="inline-flex shrink-0 text-warning"
          title={t('shell.project.unavailableHelp')}
        >
          <AlertTriangle aria-hidden="true" className="size-4" />
          <span className="sr-only">{t('shell.project.unavailableHelp')}</span>
        </span>
      )}
      {/* Só existe quando TEM contexto para mostrar: um `div` vazio carregando
          `aria-label` é um grupo sem conteúdo para quem usa leitor de tela (e
          `aria-prohibited-attr` no axe). Antes ele nascia sempre que havia projeto
          ativo, mesmo sem etapa e sem ninguém trabalhando. */}
      {activeProject && (activePhase !== null || workingCount > 0) && (
        <div
          aria-label={t('shell.context.label')}
          className="hidden min-w-0 items-center gap-2 text-xs text-foreground-muted lg:flex"
        >
          {activePhase && (
            <span className="truncate">{t('shell.context.phase', { name: activePhase.name })}</span>
          )}
          {activePhase && workingCount > 0 && <span aria-hidden="true">•</span>}
          {workingCount > 0 && (
            <span className="whitespace-nowrap">
              {t('shell.context.agentsWorking', { count: workingCount })}
            </span>
          )}
        </div>
      )}
    </div>
  );
}
