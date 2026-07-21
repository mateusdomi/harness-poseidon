import type {
  ConfigurationState,
  ProjectReadinessSnapshot,
  ReadinessBlocker,
  ReadinessNextAction,
  ReadinessStep,
  ReadinessStepContract,
} from '../contracts/readiness';

/**
 * Espelho fiel do `ReadinessEvaluator` do backend (ADR-017) para o modo mock.
 *
 * Existe porque o mock precisa produzir o MESMO read model que o Host real —
 * mesmos estados, códigos de bloqueio, rotas e `messageCode`. Se o avaliador
 * canônico mudar, este arquivo muda junto (o contrato Zod e os testes de
 * drift protegem o formato).
 *
 * Regra do mock: dependência presente é `Simulated`, nunca `Ready` — fixtures
 * não são execução real.
 */

export interface MockReadinessInputs {
  projectId: string;
  organizationReady: boolean;
  /** Conta ativa de provedor habilitado (null = ausente). */
  accountId: string | null;
  /** Modelo habilitado de provedor habilitado (null = ausente). */
  modelId: string | null;
  workflowBound: boolean;
  chiefAgentId: string | null;
  chiefHealthy: boolean;
  chiefModelResolves: boolean;
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

const STATE_TOKENS: Record<ConfigurationState, string> = {
  Unconfigured: 'unconfigured',
  Simulated: 'simulated',
  Configured: 'configured',
  Ready: 'ready',
  Degraded: 'degraded',
  Unavailable: 'unavailable',
};

function executionModeOf(state: ConfigurationState): string {
  if (state === 'Simulated') return 'simulated';
  if (state === 'Unconfigured') return 'unconfigured';
  return 'real';
}

/** Ordem de "elo mais fraco" usada para o estado geral. */
const STATE_RANK: Record<ConfigurationState, number> = {
  Unavailable: 0,
  Unconfigured: 1,
  Degraded: 2,
  Simulated: 3,
  Configured: 4,
  Ready: 5,
};

function step(
  id: ReadinessStep,
  capability: string,
  state: ConfigurationState,
  options: {
    relatedIds?: string[];
    blockers?: ReadinessBlocker[];
    nextAction?: ReadinessNextAction | null;
  } = {},
): ReadinessStepContract {
  return {
    step: id,
    state,
    executionMode: executionModeOf(state),
    capability,
    messageCode: `readiness.${STEP_TOKENS[id]}.${STATE_TOKENS[state]}`,
    relatedIds: options.relatedIds ?? [],
    blockers: options.blockers ?? [],
    nextAction: options.nextAction ?? null,
  };
}

const blocker = (code: string, relatedIds: string[] = []): ReadinessBlocker => ({
  code,
  relatedIds,
});
const action = (
  code: string,
  route: string,
  resourceId: string | null = null,
): ReadinessNextAction => ({ code, route, resourceId });

export function buildMockReadinessSnapshot(
  inputs: MockReadinessInputs,
): ProjectReadinessSnapshot {
  const accountPresent = inputs.accountId !== null;
  const modelPresent = inputs.modelId !== null;
  const chiefPresent = inputs.chiefAgentId !== null;
  const chiefIds = inputs.chiefAgentId ? [inputs.chiefAgentId] : [];

  const steps: ReadinessStepContract[] = [];

  // Perfil e organização: no mock a sessão já tem perfil ativo.
  steps.push(step('ProfileReady', 'identity.profile', 'Ready'));
  steps.push(
    inputs.organizationReady
      ? step('OrganizationReady', 'organizations', 'Ready')
      : step('OrganizationReady', 'organizations', 'Unconfigured', {
          blockers: [blocker('organization.missing')],
          nextAction: action('organization.create', '/organizations'),
        }),
  );

  // Projeto: sem organização, a etapa é bloqueada pela pré-condição.
  steps.push(
    !inputs.organizationReady
      ? step('ProjectReady', 'projects', 'Unconfigured', {
          blockers: [blocker('organization.required')],
          nextAction: action('organization.create', '/organizations'),
        })
      : step('ProjectReady', 'projects', 'Ready', { relatedIds: [inputs.projectId] }),
  );

  // Provedor/modelo presentes no mock são simulados, nunca reais.
  steps.push(
    accountPresent
      ? step('ProviderAccountReady', 'providers.accounts', 'Simulated', {
          relatedIds: [inputs.accountId!],
        })
      : step('ProviderAccountReady', 'providers.accounts', 'Unconfigured', {
          blockers: [blocker('provider_account.missing')],
          nextAction: action('provider.connectAccount', '/providers'),
        }),
  );
  steps.push(
    modelPresent
      ? step('ModelReady', 'providers.models', 'Simulated', { relatedIds: [inputs.modelId!] })
      : step('ModelReady', 'providers.models', 'Unconfigured', {
          blockers: [blocker('model.none_chat_enabled')],
          nextAction: action('model.enable', '/providers'),
        }),
  );

  steps.push(
    inputs.workflowBound
      ? step('WorkflowReady', 'workflows', 'Ready')
      : step('WorkflowReady', 'workflows', 'Unconfigured', {
          blockers: [blocker('workflow.unbound')],
          nextAction: action('workflow.bind', '/workflows'),
        }),
  );

  // Chief: ausência e modelo não resolvido são estados distintos.
  if (!chiefPresent) {
    steps.push(
      step('ChiefDefinitionReady', 'agents.chief', 'Unconfigured', {
        blockers: [blocker('chief.missing')],
        nextAction: action('chief.configureModel', '/agents'),
      }),
    );
  } else if (!inputs.chiefModelResolves) {
    steps.push(
      step('ChiefDefinitionReady', 'agents.chief', 'Unconfigured', {
        relatedIds: chiefIds,
        blockers: [blocker('chief.model_unresolved')],
        nextAction: action('chief.configureModel', '/agents', inputs.chiefAgentId),
      }),
    );
  } else {
    steps.push(
      step('ChiefDefinitionReady', 'agents.chief', 'Simulated', { relatedIds: chiefIds }),
    );
  }

  if (!chiefPresent) {
    steps.push(
      step('AgentPoolReady', 'agents.pool', 'Unconfigured', {
        blockers: [blocker('agent.unavailable')],
        nextAction: action('chief.configureModel', '/agents'),
      }),
    );
  } else if (!inputs.chiefHealthy) {
    steps.push(
      step('AgentPoolReady', 'agents.pool', 'Degraded', {
        relatedIds: chiefIds,
        blockers: [blocker('agent.degraded', chiefIds)],
        nextAction: action('agent.recover', '/agents', inputs.chiefAgentId),
      }),
    );
  } else {
    steps.push(step('AgentPoolReady', 'agents.pool', 'Simulated', { relatedIds: chiefIds }));
  }

  // Execução: qualquer dependência ausente bloqueia; simulada rebaixa.
  const blockers: ReadinessBlocker[] = [];
  if (!accountPresent) blockers.push(blocker('provider_account.missing'));
  if (!modelPresent) blockers.push(blocker('model.none_chat_enabled'));
  if (!inputs.workflowBound) blockers.push(blocker('workflow.unbound'));
  if (!chiefPresent || !inputs.chiefModelResolves) {
    blockers.push(blocker('chief.model_unresolved'));
  }
  const chiefDegraded = chiefPresent && inputs.chiefModelResolves && !inputs.chiefHealthy;
  if (chiefDegraded) blockers.push(blocker('agent.degraded', chiefIds));

  let executionState: ConfigurationState;
  if (blockers.length === 0) {
    // Todas presentes, mas o mock é simulado por definição.
    executionState = 'Simulated';
  } else if (chiefDegraded && blockers.length === 1) {
    executionState = 'Degraded';
  } else {
    executionState = 'Unconfigured';
  }

  const executionAction =
    blockers.length === 0
      ? action('conversation.start', '/projects', inputs.projectId)
      : blockers[0].code === 'provider_account.missing'
        ? action('provider.connectAccount', '/providers')
        : blockers[0].code === 'model.none_chat_enabled'
          ? action('model.enable', '/providers')
          : blockers[0].code === 'workflow.unbound'
            ? action('workflow.bind', '/workflows')
            : blockers[0].code === 'agent.degraded'
              ? action('agent.recover', '/agents', inputs.chiefAgentId)
              : action('chief.configureModel', '/agents');

  steps.push(
    step('ExecutionReady', 'execution', executionState, {
      blockers,
      nextAction: executionAction,
    }),
  );

  const overallState = steps
    .map((entry) => entry.state)
    .reduce((left, right) => (STATE_RANK[left] <= STATE_RANK[right] ? left : right));

  // Ações das etapas não prontas, deduplicadas por código (ordem preservada).
  const nextActions: ReadinessNextAction[] = [];
  for (const entry of steps) {
    if (entry.state === 'Ready' || entry.nextAction === null) continue;
    if (nextActions.some((existing) => existing.code === entry.nextAction!.code)) continue;
    nextActions.push(entry.nextAction);
  }

  return { projectId: inputs.projectId, overallState, steps, nextActions };
}
