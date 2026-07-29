import type { Agent, AgentDefinition } from '@/api';

const ATTENTION_STATES = new Set<Agent['state']>(['error', 'outOfQuota']);

export interface TeamNucleus {
  key: string;
  name: string;
  total: number;
  working: number;
  available: number;
  attention: number;
}

export interface TeamActivitySummary {
  chief: Agent | null;
  nuclei: TeamNucleus[];
  working: Agent[];
  attention: Agent[];
  available: number;
  hiddenWorking: number;
  hiddenAttention: number;
}

/**
 * Projeção compacta para o Dashboard: uma única chefe e especialistas
 * agrupados por núcleo. A lista nominal é deliberadamente curta para continuar
 * legível quando a equipe ultrapassar 30 instâncias.
 */
export function summarizeTeamActivity(
  agents: readonly Agent[],
  definitions: readonly AgentDefinition[],
  chiefAgentId: string | null,
): TeamActivitySummary {
  const definitionById = new Map(definitions.map((definition) => [definition.id, definition]));
  const declaredChief = chiefAgentId
    ? agents.find((agent) => agent.id === chiefAgentId) ?? null
    : null;
  const chief =
    declaredChief ??
    agents.find((agent) => definitionById.get(agent.definitionId)?.role === 'chief') ??
    null;
  const specialists = agents.filter(
    (agent) =>
      agent.id !== chief?.id &&
      definitionById.get(agent.definitionId)?.role !== 'chief',
  );
  const grouped = new Map<string, TeamNucleus>();

  for (const agent of specialists) {
    const definition = definitionById.get(agent.definitionId);
    const name = definition?.team?.trim() || definition?.specialty?.trim() || 'Outros';
    const key = name.toLocaleLowerCase();
    const nucleus = grouped.get(key) ?? {
      key,
      name,
      total: 0,
      working: 0,
      available: 0,
      attention: 0,
    };
    nucleus.total += 1;
    if (agent.state === 'working') nucleus.working += 1;
    if (ATTENTION_STATES.has(agent.state)) nucleus.attention += 1;
    else nucleus.available += 1;
    grouped.set(key, nucleus);
  }

  const working = specialists.filter((agent) => agent.state === 'working');
  const attention = specialists.filter((agent) => ATTENTION_STATES.has(agent.state));

  return {
    chief,
    nuclei: [...grouped.values()].sort(
      (left, right) => right.total - left.total || left.name.localeCompare(right.name),
    ),
    working: working.slice(0, 4),
    attention: attention.slice(0, 3),
    available: specialists.length - attention.length,
    hiddenWorking: Math.max(0, working.length - 4),
    hiddenAttention: Math.max(0, attention.length - 3),
  };
}

