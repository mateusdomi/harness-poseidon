import { useTranslation } from 'react-i18next';

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
  const { activeProject } = useActiveProject();
  const projectId = activeProject?.id ?? null;
  const { phases } = useCockpitWorkflow(projectId);
  const agentsQuery = useCockpitAgents(projectId);

  if (!activeProject) return null;

  const activePhase = phases.find((phase) => phase.state === 'active') ?? null;
  const workingCount = (agentsQuery.data ?? []).filter((agent) => agent.state === 'working').length;

  return (
    <div
      aria-label={t('shell.context.label')}
      className="hidden min-w-0 items-center gap-2 text-xs text-foreground-muted lg:flex"
    >
      <span className="truncate text-sm font-medium text-foreground">{activeProject.name}</span>
      {activePhase && (
        <>
          <span aria-hidden="true" className="select-none">
            •
          </span>
          <span className="truncate">{t('shell.context.phase', { name: activePhase.name })}</span>
        </>
      )}
      {workingCount > 0 && (
        <>
          <span aria-hidden="true" className="select-none">
            •
          </span>
          <span>{t('shell.context.agentsWorking', { count: workingCount })}</span>
        </>
      )}
    </div>
  );
}
