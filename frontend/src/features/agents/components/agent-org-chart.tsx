import { useTranslation } from 'react-i18next';

import type { Agent, AgentDefinition, Attempt, Skill, Task } from '@/api';
import { AgentCard } from '@/features/agents/components/agent-card';
import {
  agentSkills,
  definitionOf,
  deriveAgentMetrics,
  type AgentTeam,
} from '@/features/agents/lib/agents-derive';
import { useMediaQuery } from '@/features/board/hooks/use-media-query';

export interface AgentOrgChartProps {
  team: AgentTeam;
  definitions: AgentDefinition[];
  skills: Skill[];
  tasks: Task[];
  attempts: Attempt[];
  onSelect: (agent: Agent) => void;
}

/**
 * Organograma da equipe do projeto ativo.
 *
 * Responsividade (D-022): UMA árvore no DOM por breakpoint, escolhida via
 * `useMediaQuery` (matchMedia) — nunca duas regiões escondidas por CSS,
 * para não duplicar headings/cards na árvore de acessibilidade.
 * - Desktop (lg+): hierarquia visual — chefe centrado no topo, conector
 *   (CSS puro, `aria-hidden`) e especialistas em grade abaixo;
 * - Mobile (<lg): lista agrupada com grupos "Chefe" e "Especialistas".
 */
export function AgentOrgChart({
  team,
  definitions,
  skills,
  tasks,
  attempts,
  onSelect,
}: AgentOrgChartProps) {
  const { t } = useTranslation();
  const isDesktop = useMediaQuery('(min-width: 1024px)');

  function renderCard(agent: Agent) {
    const definition = definitionOf(agent, definitions);
    return (
      <AgentCard
        agent={agent}
        definition={definition}
        skills={agentSkills(definition, skills)}
        metrics={deriveAgentMetrics(agent, tasks, attempts)}
        onSelect={onSelect}
      />
    );
  }

  if (isDesktop) {
    return (
      <div role="group" aria-label={t('agents.tree.label')} className="flex flex-col">
        {team.chief && (
          <>
            <div className="flex justify-center">
              <div className="w-full max-w-sm">{renderCard(team.chief)}</div>
            </div>
            {team.specialists.length > 0 && (
              <>
                {/* Conector visual: tronco vertical + barra horizontal + galhos. */}
                <div aria-hidden="true" className="mx-auto h-6 w-px bg-border-strong" />
                <div aria-hidden="true" className="relative mx-auto h-px w-2/3 bg-border-strong">
                  <span className="absolute left-0 top-0 h-3 w-px bg-border-strong" />
                  <span className="absolute left-1/2 top-0 h-3 w-px bg-border-strong" />
                  <span className="absolute right-0 top-0 h-3 w-px bg-border-strong" />
                </div>
                <div aria-hidden="true" className="h-3" />
              </>
            )}
          </>
        )}
        {team.specialists.length > 0 && (
          <ul className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
            {team.specialists.map((agent) => (
              <li key={agent.id}>{renderCard(agent)}</li>
            ))}
          </ul>
        )}
      </div>
    );
  }

  return (
    <div role="group" aria-label={t('agents.tree.label')} className="flex flex-col gap-6">
      {team.chief && (
        <section className="flex flex-col gap-3">
          <h2 className="font-heading text-lg font-semibold">{t('agents.groups.chief')}</h2>
          {renderCard(team.chief)}
        </section>
      )}
      {team.specialists.length > 0 && (
        <section className="flex flex-col gap-3">
          <h2 className="font-heading text-lg font-semibold">
            {t('agents.groups.specialists')}
          </h2>
          <ul className="flex flex-col gap-3">
            {team.specialists.map((agent) => (
              <li key={agent.id}>{renderCard(agent)}</li>
            ))}
          </ul>
        </section>
      )}
    </div>
  );
}
