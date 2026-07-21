import type {
  ConfigurationState,
  ProjectReadinessSnapshot,
  ReadinessNextAction,
  ReadinessStep,
  ReadinessStepContract,
} from '@/api';

/**
 * Apresentação do golden path a partir do read model canônico de prontidão
 * (`ProjectReadinessSnapshot`, ADR-017).
 *
 * Este módulo NÃO decide prontidão — o backend decide. Aqui só traduzimos o
 * snapshot para o que a lista precisa exibir: qual etapa está concluída, qual
 * é a atual, qual está bloqueada e por quê.
 */

/** Ordem canônica das etapas, como o backend publica. */
export const GOLDEN_PATH_STEPS: ReadinessStep[] = [
  'ProfileReady',
  'OrganizationReady',
  'ProjectReady',
  'ProviderAccountReady',
  'ModelReady',
  'WorkflowReady',
  'ChiefDefinitionReady',
  'AgentPoolReady',
  'ExecutionReady',
];

/**
 * - `done`: etapa satisfeita (`Ready`, ou `Simulated`/`Configured` — que
 *   executam, ainda que em modo simulado, e são sinalizados como tal).
 * - `current`: primeira etapa acionável.
 * - `blocked`: tem bloqueador declarado pelo backend.
 * - `pending`: não pronta, sem bloqueador e sem ser o foco atual.
 */
export type StepStatus = 'done' | 'current' | 'blocked' | 'pending';

export interface GoldenPathStep {
  id: ReadinessStep;
  status: StepStatus;
  state: ConfigurationState;
  /** `real` | `simulated` | `unconfigured`, direto do contrato. */
  executionMode: string;
  /** Código i18n da mensagem da etapa (nunca texto de domínio pronto). */
  messageCode: string;
  /** Códigos de bloqueio declarados pelo backend. */
  blockerCodes: string[];
  nextAction: ReadinessNextAction | null;
}

export interface GoldenPathState {
  steps: GoldenPathStep[];
  current: ReadinessStep | null;
  doneCount: number;
  totalCount: number;
  complete: boolean;
  overallState: ConfigurationState;
  /** Ações agregadas das etapas não prontas, deduplicadas pelo backend. */
  nextActions: ReadinessNextAction[];
  /** Execução liberada: a etapa final executa (real ou simulada). */
  canExecute: boolean;
}

/** Estados que significam "esta dependência não impede a execução". */
const SATISFIED_STATES: ConfigurationState[] = ['Ready', 'Configured', 'Simulated'];

export function isSatisfied(state: ConfigurationState): boolean {
  return SATISFIED_STATES.includes(state);
}

function toStep(contract: ReadinessStepContract, currentAssigned: boolean): GoldenPathStep {
  const satisfied = isSatisfied(contract.state);
  let status: StepStatus;
  if (satisfied) {
    status = 'done';
  } else if (contract.blockers.length > 0) {
    status = 'blocked';
  } else if (!currentAssigned) {
    status = 'current';
  } else {
    status = 'pending';
  }

  return {
    id: contract.step,
    status,
    state: contract.state,
    executionMode: contract.executionMode,
    messageCode: contract.messageCode,
    blockerCodes: contract.blockers.map((blocker) => blocker.code),
    nextAction: contract.nextAction,
  };
}

/**
 * Traduz o snapshot canônico para a apresentação da lista.
 *
 * A "etapa atual" é a primeira não satisfeita — inclusive quando ela tem
 * bloqueador, porque o backend já entrega a ação que resolve o bloqueio. Isso
 * evita a lista ficar sem foco quando tudo que falta está bloqueado.
 */
export function deriveGoldenPath(snapshot: ProjectReadinessSnapshot): GoldenPathState {
  const ordered = GOLDEN_PATH_STEPS.map((id) =>
    snapshot.steps.find((entry) => entry.step === id),
  ).filter((entry): entry is ReadinessStepContract => entry !== undefined);

  const firstUnsatisfied = ordered.find((entry) => !isSatisfied(entry.state)) ?? null;

  const steps = ordered.map((entry) => {
    const step = toStep(entry, true);
    if (firstUnsatisfied && entry.step === firstUnsatisfied.step) {
      // A primeira pendente é sempre o foco, mesmo bloqueada.
      return { ...step, status: 'current' as StepStatus };
    }
    return step;
  });

  const doneCount = steps.filter((entry) => entry.status === 'done').length;
  const execution = ordered.find((entry) => entry.step === 'ExecutionReady') ?? null;

  return {
    steps,
    current: firstUnsatisfied?.step ?? null,
    doneCount,
    totalCount: steps.length,
    complete: firstUnsatisfied === null,
    overallState: snapshot.overallState,
    nextActions: snapshot.nextActions,
    canExecute: execution !== null && isSatisfied(execution.state),
  };
}

export interface PreProjectInput {
  hasProfile: boolean;
  hasOrganization: boolean;
  hasProject: boolean;
}

/**
 * Snapshot local para a fase ANTERIOR ao projeto.
 *
 * `GET /projects/{id}/readiness` responde 404 sem projeto, então as três
 * primeiras etapas são montadas aqui — no mesmo formato e com os MESMOS
 * códigos do contrato, para a UI ter um único caminho de renderização. As
 * etapas seguintes ficam `Unconfigured` e bloqueadas pela ausência do projeto:
 * fail-closed, nunca otimista. Lacuna registrada em `HANDOFF_API.md`.
 */
export function preProjectSnapshot(input: PreProjectInput): ProjectReadinessSnapshot {
  const mk = (
    step: ReadinessStep,
    capability: string,
    state: ConfigurationState,
    blockerCode: string | null,
    nextAction: ReadinessNextAction | null,
  ): ReadinessStepContract => ({
    step,
    state,
    executionMode: state === 'Ready' ? 'real' : 'unconfigured',
    capability,
    messageCode: `readiness.${STEP_TOKENS[step]}.${state === 'Ready' ? 'ready' : 'unconfigured'}`,
    relatedIds: [],
    blockers: blockerCode ? [{ code: blockerCode, relatedIds: [] }] : [],
    nextAction,
  });

  const createOrganization: ReadinessNextAction = {
    code: 'organization.create',
    route: '/organizations',
    resourceId: null,
  };
  const createProject: ReadinessNextAction = {
    code: 'project.create',
    route: '/projects',
    resourceId: null,
  };

  const steps: ReadinessStepContract[] = [
    mk(
      'ProfileReady',
      'identity.profile',
      input.hasProfile ? 'Ready' : 'Unconfigured',
      input.hasProfile ? null : 'profile.missing',
      input.hasProfile ? null : { code: 'profile.create', route: '/onboarding', resourceId: null },
    ),
    mk(
      'OrganizationReady',
      'organizations',
      input.hasOrganization ? 'Ready' : 'Unconfigured',
      input.hasOrganization ? null : 'organization.missing',
      input.hasOrganization ? null : createOrganization,
    ),
    // Precondição do contrato: sem organização, projeto é bloqueado por ela.
    !input.hasOrganization
      ? mk('ProjectReady', 'projects', 'Unconfigured', 'organization.required', createOrganization)
      : mk(
          'ProjectReady',
          'projects',
          input.hasProject ? 'Ready' : 'Unconfigured',
          input.hasProject ? null : 'project.missing',
          input.hasProject ? null : createProject,
        ),
  ];

  // Sem projeto, as demais dependências não são avaliáveis: bloqueamos por
  // ausência de projeto em vez de presumir qualquer prontidão.
  const REMAINING: [ReadinessStep, string][] = [
    ['ProviderAccountReady', 'providers.accounts'],
    ['ModelReady', 'providers.models'],
    ['WorkflowReady', 'workflows'],
    ['ChiefDefinitionReady', 'agents.chief'],
    ['AgentPoolReady', 'agents.pool'],
    ['ExecutionReady', 'execution'],
  ];
  for (const [step, capability] of REMAINING) {
    steps.push(mk(step, capability, 'Unconfigured', 'project.missing', createProject));
  }

  const nextActions: ReadinessNextAction[] = [];
  for (const entry of steps) {
    if (entry.state === 'Ready' || entry.nextAction === null) continue;
    if (nextActions.some((existing) => existing.code === entry.nextAction!.code)) continue;
    nextActions.push(entry.nextAction);
  }

  return {
    projectId: null,
    overallState: 'Unconfigured',
    steps,
    nextActions,
  };
}

const STEP_TOKENS: Record<ReadinessStep, string> = {
  ProfileReady: 'profile',
  OrganizationReady: 'organization',
  ProjectReady: 'project',
  ProviderAccountReady: 'providerAccount',
  ModelReady: 'model',
  WorkflowReady: 'workflow',
  ChiefDefinitionReady: 'chiefDefinition',
  AgentPoolReady: 'agentPool',
  ExecutionReady: 'execution',
};
