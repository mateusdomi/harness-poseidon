import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { FilterX, Users } from 'lucide-react';

import { AGENT_STATES, type AgentState, type Ulid } from '@/api';
import { Button, Card, CardContent, Select, Skeleton } from '@/design-system';
import { AgentDetail } from '@/features/agents/components/agent-detail';
import { AgentExecutionRoster } from '@/features/agents/components/agent-execution-roster';
import { AgentOrgChart } from '@/features/agents/components/agent-org-chart';
import { FleetOrgChart } from '@/features/agents/components/fleet-org-chart';
import { AgentsUtilizationChart } from '@/features/agents/components/agents-utilization-chart';
import { useAgentsData, useAgentsRealtime } from '@/features/agents/hooks/use-agents';
import { useAgentRoster } from '@/features/agents/hooks/use-agent-roster';
import {
  definitionOf,
  distinctTeams,
  filterSpecialists,
  teamOfProject,
  type TeamFilter,
} from '@/features/agents/lib/agents-derive';
import { useActiveProject } from '@/features/shared/hooks/use-active-project';
import { useNow } from '@/features/shared/hooks/use-now';

/**
 * Equipe de agentes do projeto ativo: organograma (chefe no topo,
 * especialistas agrupados pelo time da definição) com cards de
 * estado/capacidades/métricas, filtros de estado e time (o chefe
 * permanece) e detalhe em modal. `agent.statusChanged` (stream global)
 * atualiza os badges em tempo real.
 */
export default function UagentsPage() {
  const { t } = useTranslation();
  const { projects, activeProject, setActiveProject, isPending, isError, refetch } =
    useActiveProject();
  const data = useAgentsData();
  const roster = useAgentRoster();
  useAgentsRealtime(activeProject?.id ?? null);
  const now = useNow();

  const [selectedAgentId, setSelectedAgentId] = useState<Ulid | null>(null);
  const [stateFilter, setStateFilter] = useState<AgentState | 'all'>('all');
  const [teamFilter, setTeamFilter] = useState<TeamFilter>('all');

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

  const specialists = team?.specialists ?? [];
  const { teams, hasGeneral } = distinctTeams(specialists, data.definitions);
  const filteredSpecialists = filterSpecialists(
    specialists,
    data.definitions,
    stateFilter,
    teamFilter,
  );
  const filtersActive = stateFilter !== 'all' || teamFilter !== 'all';
  const filteredTeam = team ? { ...team, specialists: filteredSpecialists } : null;

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="font-heading text-2xl font-semibold">{t('features.agents.title')}</h1>
        {!loading && !errored && activeProject && (
          <span className="text-sm text-foreground-muted">
            {t('agents.executionCount', { count: roster.data?.length ?? 0 })}
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

      <AgentExecutionRoster />
      <FleetOrgChart />

      {loading ? (
        <div className="flex flex-col gap-4" role="status" aria-label={t('common.states.loading')}>
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
        filteredTeam && (
          <>
            <div className="flex flex-wrap items-center gap-3">
              <div className="flex items-center gap-2">
                <label htmlFor="agents-filter-state" className="text-sm text-foreground-muted">
                  {t('agents.filters.state.label')}
                </label>
                <Select
                  id="agents-filter-state"
                  className="w-auto min-w-40"
                  value={stateFilter}
                  onChange={(event) => setStateFilter(event.target.value as AgentState | 'all')}
                >
                  <option value="all">{t('agents.filters.state.all')}</option>
                  {AGENT_STATES.map((state) => (
                    <option key={state} value={state}>
                      {t(`status.agentState.${state}`)}
                    </option>
                  ))}
                </Select>
              </div>
              <div className="flex items-center gap-2">
                <label htmlFor="agents-filter-team" className="text-sm text-foreground-muted">
                  {t('agents.filters.team.label')}
                </label>
                <Select
                  id="agents-filter-team"
                  className="w-auto min-w-40"
                  value={teamFilter}
                  onChange={(event) => setTeamFilter(event.target.value)}
                >
                  <option value="all">{t('agents.filters.team.all')}</option>
                  {teams.map((teamName) => (
                    <option key={teamName} value={teamName}>
                      {teamName}
                    </option>
                  ))}
                  {hasGeneral && <option value="none">{t('agents.teams.general')}</option>}
                </Select>
              </div>
            </div>

            {filtersActive && filteredSpecialists.length === 0 && (
              <Card>
                <CardContent className="flex flex-col items-center gap-3 p-6 text-center">
                  <FilterX aria-hidden="true" className="size-8 text-foreground-muted" />
                  <h2 className="font-heading text-lg font-semibold">
                    {t('agents.filteredEmpty.title')}
                  </h2>
                  <p className="text-sm text-foreground-muted">{t('agents.filteredEmpty.body')}</p>
                </CardContent>
              </Card>
            )}

            {/* Utilização da equipe INTEIRA (chefe + especialistas), independente
                dos filtros de estado/time aplicados ao organograma abaixo. */}
            <AgentsUtilizationChart
              agents={[filteredTeam.chief, ...(team?.specialists ?? [])].filter(
                (agent): agent is NonNullable<typeof agent> => agent !== null,
              )}
            />

            <AgentOrgChart
              team={filteredTeam}
              definitions={data.definitions}
              skills={data.skills}
              tasks={data.tasks}
              attempts={data.attempts}
              onSelect={(agent) => setSelectedAgentId(agent.id)}
            />
          </>
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
