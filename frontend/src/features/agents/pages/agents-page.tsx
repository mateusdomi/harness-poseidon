import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { Users } from 'lucide-react';

import type { Ulid } from '@/api';
import { Button, Card, CardContent, Select, Skeleton } from '@/design-system';
import { AgentDetail } from '@/features/agents/components/agent-detail';
import { AgentOrgChart } from '@/features/agents/components/agent-org-chart';
import { useAgentsData, useAgentsRealtime } from '@/features/agents/hooks/use-agents';
import { definitionOf, teamOfProject } from '@/features/agents/lib/agents-derive';
import { useActiveProject } from '@/features/shared/hooks/use-active-project';
import { useNow } from '@/features/shared/hooks/use-now';

/**
 * Equipe de agentes do projeto ativo: organograma (chefe no topo,
 * especialistas abaixo) com cards de estado/capacidades/métricas e
 * detalhe em modal. `agent.statusChanged` (stream global) atualiza
 * os badges em tempo real.
 */
export default function UagentsPage() {
  const { t } = useTranslation();
  const { projects, activeProject, setActiveProject, isPending, isError, refetch } =
    useActiveProject();
  const data = useAgentsData();
  useAgentsRealtime();
  const now = useNow();

  const [selectedAgentId, setSelectedAgentId] = useState<Ulid | null>(null);

  const loading = isPending || data.isPending;
  const errored = isError || data.isError;

  function retryAll() {
    refetch();
    data.refetch();
  }

  const team = activeProject ? teamOfProject(activeProject, data.agents) : null;
  const teamCount = (team?.chief ? 1 : 0) + (team?.specialists.length ?? 0);
  // Resolve a instância fresca: o modal reflete mudanças de estado em tempo real.
  const selectedAgent = data.agents.find((agent) => agent.id === selectedAgentId) ?? null;

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="font-heading text-2xl font-semibold">{t('features.agents.title')}</h1>
        {!loading && !errored && activeProject && (
          <span className="text-sm text-foreground-muted">
            {t('agents.count', { count: teamCount })}
          </span>
        )}
        {projects.length > 0 && (
          <div className="ml-auto flex items-center gap-2">
            <label htmlFor="agents-project" className="text-sm text-foreground-muted">
              {t('cockpit.projectSelector.label')}
            </label>
            <Select
              id="agents-project"
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
      </div>

      {loading ? (
        <div className="flex flex-col gap-4" aria-label={t('common.states.loading')}>
          <Skeleton className="mx-auto h-56 w-full max-w-sm" />
          <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
            {Array.from({ length: 3 }, (_, index) => (
              <Skeleton key={index} className="h-56 w-full" />
            ))}
          </div>
        </div>
      ) : errored ? (
        <div className="flex flex-col items-start gap-3">
          <p role="alert" className="text-sm text-error">
            {t('common.states.errorBody')}
          </p>
          <Button type="button" variant="outline" onClick={retryAll}>
            {t('common.actions.retry')}
          </Button>
        </div>
      ) : !activeProject ? (
        <Card>
          <CardContent className="flex flex-col items-start gap-3 p-6">
            <p className="text-sm text-foreground-muted">{t('agents.noProject.body')}</p>
            <Button asChild>
              <Link to="/projects">{t('agents.noProject.cta')}</Link>
            </Button>
          </CardContent>
        </Card>
      ) : teamCount === 0 ? (
        <Card>
          <CardContent className="flex flex-col items-center gap-3 p-6 text-center">
            <Users aria-hidden="true" className="size-8 text-foreground-muted" />
            <h2 className="font-heading text-lg font-semibold">{t('agents.empty.title')}</h2>
            <p className="text-sm text-foreground-muted">{t('agents.empty.body')}</p>
          </CardContent>
        </Card>
      ) : (
        team && (
          <AgentOrgChart
            team={team}
            definitions={data.definitions}
            skills={data.skills}
            tasks={data.tasks}
            attempts={data.attempts}
            onSelect={(agent) => setSelectedAgentId(agent.id)}
          />
        )
      )}

      {selectedAgent && (
        <AgentDetail
          agent={selectedAgent}
          definition={definitionOf(selectedAgent, data.definitions)}
          skills={data.skills}
          tools={data.tools}
          models={data.models}
          tasks={data.tasks}
          attempts={data.attempts}
          auditEvents={data.auditEvents}
          now={now}
          onClose={() => setSelectedAgentId(null)}
        />
      )}
    </div>
  );
}
