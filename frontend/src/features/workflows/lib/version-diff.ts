import type { WorkflowPhaseConfig, WorkflowVersion } from '@/api';

/**
 * Diff ESTRUTURAL entre duas versões de workflow (FR-4): fases
 * adicionadas/removidas/alteradas, gates, pesos, documentos, agentes,
 * skills, ferramentas, critérios, dependências, condições, transições e
 * modo padrão. Não é diff de texto — cada mudança é um item estruturado
 * com rótulo i18n (`workflows.compare.fields.*`) resolvido na render.
 */

export interface ListFieldChange {
  /** Chave i18n do campo (`workflows.compare.fields.*`). */
  field: string;
  added: string[];
  removed: string[];
}

export interface ValueFieldChange {
  field: string;
  from: string;
  to: string;
}

export interface PhaseDiff {
  phase: string;
  lists: ListFieldChange[];
  values: ValueFieldChange[];
}

export interface WorkflowVersionDiff {
  phasesAdded: string[];
  phasesRemoved: string[];
  /** Fase presente nas duas versões, mas em posição diferente. */
  phasesReordered: string[];
  phasesChanged: PhaseDiff[];
  modeChange: { from: string; to: string } | null;
  identical: boolean;
}

function diffList(before: string[], after: string[]): { added: string[]; removed: string[] } {
  const beforeSet = new Set(before);
  const afterSet = new Set(after);
  return {
    added: after.filter((item) => !beforeSet.has(item)),
    removed: before.filter((item) => !afterSet.has(item)),
  };
}

function pushList(
  target: ListFieldChange[],
  field: string,
  before: string[],
  after: string[],
): void {
  const { added, removed } = diffList(before, after);
  if (added.length > 0 || removed.length > 0) target.push({ field, added, removed });
}

function pushValue(
  target: ValueFieldChange[],
  field: string,
  before: string,
  after: string,
): void {
  if (before !== after) target.push({ field, from: before, to: after });
}

const EMPTY_CONFIG: WorkflowPhaseConfig = {
  documentKinds: [],
  progressWeight: 0,
  allowedAgentDefinitionIds: [],
};

/** Compara duas versões (qualquer estado) e retorna o diff estrutural. */
export function diffWorkflowVersions(
  before: WorkflowVersion,
  after: WorkflowVersion,
): WorkflowVersionDiff {
  const beforePhases = before.phases;
  const afterPhases = after.phases;
  const beforeSet = new Set(beforePhases);
  const afterSet = new Set(afterPhases);

  const phasesAdded = afterPhases.filter((phase) => !beforeSet.has(phase));
  const phasesRemoved = beforePhases.filter((phase) => !afterSet.has(phase));
  const phasesReordered = afterPhases.filter(
    (phase) =>
      beforeSet.has(phase) && beforePhases.indexOf(phase) !== afterPhases.indexOf(phase),
  );

  const phasesChanged: PhaseDiff[] = [];
  for (const phase of afterPhases) {
    if (!beforeSet.has(phase)) continue;
    const beforeConfig = before.phaseConfigs?.[phase] ?? EMPTY_CONFIG;
    const afterConfig = after.phaseConfigs?.[phase] ?? EMPTY_CONFIG;
    const lists: ListFieldChange[] = [];
    const values: ValueFieldChange[] = [];

    pushList(lists, 'gates', before.gatesByPhase[phase] ?? [], after.gatesByPhase[phase] ?? []);
    pushList(lists, 'documentKinds', beforeConfig.documentKinds, afterConfig.documentKinds);
    pushList(
      lists,
      'agents',
      beforeConfig.allowedAgentDefinitionIds,
      afterConfig.allowedAgentDefinitionIds,
    );
    pushList(lists, 'skills', beforeConfig.allowedSkillIds ?? [], afterConfig.allowedSkillIds ?? []);
    pushList(lists, 'tools', beforeConfig.allowedToolIds ?? [], afterConfig.allowedToolIds ?? []);
    pushList(
      lists,
      'acceptanceCriteria',
      beforeConfig.acceptanceCriteria ?? [],
      afterConfig.acceptanceCriteria ?? [],
    );
    pushList(lists, 'dependsOn', beforeConfig.dependsOn ?? [], afterConfig.dependsOn ?? []);
    pushList(
      lists,
      'entryConditions',
      beforeConfig.entryConditions ?? [],
      afterConfig.entryConditions ?? [],
    );
    pushList(
      lists,
      'exitConditions',
      beforeConfig.exitConditions ?? [],
      afterConfig.exitConditions ?? [],
    );
    pushList(lists, 'transitions', before.transitions?.[phase] ?? [], after.transitions?.[phase] ?? []);
    pushValue(
      values,
      'weight',
      String(beforeConfig.progressWeight),
      String(afterConfig.progressWeight),
    );
    pushValue(values, 'objective', beforeConfig.objective ?? '', afterConfig.objective ?? '');
    pushValue(values, 'context', beforeConfig.context ?? '', afterConfig.context ?? '');

    if (lists.length > 0 || values.length > 0) phasesChanged.push({ phase, lists, values });
  }

  const beforeMode = before.defaultOperationMode ?? null;
  const afterMode = after.defaultOperationMode ?? null;
  const modeChange =
    beforeMode !== afterMode
      ? { from: beforeMode ?? 'none', to: afterMode ?? 'none' }
      : null;

  const identical =
    phasesAdded.length === 0 &&
    phasesRemoved.length === 0 &&
    phasesReordered.length === 0 &&
    phasesChanged.length === 0 &&
    modeChange === null;

  return { phasesAdded, phasesRemoved, phasesReordered, phasesChanged, modeChange, identical };
}
