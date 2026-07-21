import type { Account, Model, Organization, Project, Provider, Workflow } from '@/api';

/**
 * Caminho dourado (golden path) do primeiro uso: a sequência mínima que leva
 * um usuário novo de "workspace vazio" até uma execução real do Chief.
 *
 * IMPORTANTE — fonte da verdade: não existe endpoint canônico de "readiness"
 * no contrato (`docs/contracts/openapi.json`). Cada etapa é DERIVADA da
 * existência de recursos reais já publicados (organizações, projetos,
 * provedores/contas, modelos, workflows) — nunca de um estado inventado no
 * frontend. As etapas `chief` e `firstRun` dependem de sinais que o backend
 * ainda não expõe de forma canônica; são heurísticas conservadoras
 * (fail-closed) e estão registradas em `docs/frontend/HANDOFF_API.md`.
 */
export const GOLDEN_PATH_STEPS = [
  'profile',
  'organization',
  'project',
  'provider',
  'model',
  'workflow',
  'chief',
  'firstRun',
] as const;

export type GoldenPathStepId = (typeof GOLDEN_PATH_STEPS)[number];

/**
 * - `done`: pré-condição real satisfeita.
 * - `current`: primeira etapa acionável (única CTA em foco).
 * - `blocked`: depende de uma etapa anterior ainda não concluída.
 * - `pending`: acionável no futuro, mas não é o foco atual.
 */
export type StepStatus = 'done' | 'current' | 'blocked' | 'pending';

/** Pré-requisitos reais de cada etapa (não meramente a ordem visual). */
export const GOLDEN_PATH_PREREQUISITES: Record<GoldenPathStepId, GoldenPathStepId[]> = {
  profile: [],
  organization: [],
  project: ['organization'],
  provider: [],
  model: ['provider'],
  workflow: ['project'],
  chief: ['provider', 'model', 'workflow'],
  firstRun: ['chief'],
};

export interface GoldenPathStep {
  id: GoldenPathStepId;
  status: StepStatus;
  /** Pré-requisitos ainda não concluídos (quando `status === 'blocked'`). */
  blockedBy: GoldenPathStepId[];
}

export interface GoldenPathState {
  steps: GoldenPathStep[];
  /** Primeira etapa acionável, ou `null` quando tudo concluído. */
  current: GoldenPathStepId | null;
  doneCount: number;
  totalCount: number;
  complete: boolean;
}

export interface GoldenPathInput {
  /** Perfil ativo selecionado na sessão. */
  hasProfile: boolean;
  organizations: Organization[];
  projects: Project[];
  /** Projeto ativo efetivo (seleção da sessão ou o primeiro da lista). */
  activeProject: Project | null;
  providers: Provider[];
  accounts: Account[];
  models: Model[];
  /** Workflow vinculado ao projeto ativo (null = nenhum). */
  activeWorkflow: Workflow | null;
  /**
   * Sinal real de que o Chief já executou no projeto ativo — hoje derivado de
   * um workflow run existente (`useProjectStarted`). Fail-closed: sem sinal,
   * a primeira execução permanece pendente.
   */
  hasChiefActivity: boolean;
}

/** Provedor pronto = ao menos um provedor habilitado com uma conta ativa. */
export function isProviderReady(providers: Provider[], accounts: Account[]): boolean {
  const enabledProviderIds = new Set(providers.filter((p) => p.enabled).map((p) => p.id));
  if (enabledProviderIds.size === 0) return false;
  return accounts.some((a) => a.state === 'active' && enabledProviderIds.has(a.providerId));
}

/** Modelo pronto = ao menos um modelo habilitado de um provedor habilitado. */
export function isModelReady(models: Model[], providers: Provider[]): boolean {
  const enabledProviderIds = new Set(providers.filter((p) => p.enabled).map((p) => p.id));
  return models.some((m) => m.enabled && enabledProviderIds.has(m.providerId));
}

/** Mapa etapa → concluída, derivado apenas de recursos reais. */
export function deriveDoneMap(input: GoldenPathInput): Record<GoldenPathStepId, boolean> {
  const providerReady = isProviderReady(input.providers, input.accounts);
  const modelReady = isModelReady(input.models, input.providers);
  const workflowReady = input.activeProject !== null && input.activeWorkflow !== null;
  // Fail-closed: o Chief só é considerado pronto quando provedor, modelo e
  // workflow estão prontos E há um projeto ativo com chefe atribuído.
  const chiefReady =
    input.activeProject !== null &&
    Boolean(input.activeProject.chiefAgentId) &&
    providerReady &&
    modelReady &&
    workflowReady;

  return {
    profile: input.hasProfile,
    organization: input.organizations.length > 0,
    project: input.projects.length > 0,
    provider: providerReady,
    model: modelReady,
    workflow: workflowReady,
    chief: chiefReady,
    firstRun: chiefReady && input.hasChiefActivity,
  };
}

/**
 * Deriva o estado completo do golden path. Puro e testável: recebe recursos
 * já carregados e devolve status por etapa, a etapa atual e o progresso.
 */
export function deriveGoldenPath(input: GoldenPathInput): GoldenPathState {
  const done = deriveDoneMap(input);

  let currentAssigned = false;
  const steps: GoldenPathStep[] = GOLDEN_PATH_STEPS.map((id) => {
    const blockedBy = GOLDEN_PATH_PREREQUISITES[id].filter((prereq) => !done[prereq]);
    if (done[id]) {
      return { id, status: 'done', blockedBy: [] };
    }
    if (blockedBy.length > 0) {
      return { id, status: 'blocked', blockedBy };
    }
    if (!currentAssigned) {
      currentAssigned = true;
      return { id, status: 'current', blockedBy: [] };
    }
    return { id, status: 'pending', blockedBy: [] };
  });

  const doneCount = steps.filter((s) => s.status === 'done').length;
  const current = steps.find((s) => s.status === 'current')?.id ?? null;

  return {
    steps,
    current,
    doneCount,
    totalCount: GOLDEN_PATH_STEPS.length,
    complete: doneCount === GOLDEN_PATH_STEPS.length,
  };
}
