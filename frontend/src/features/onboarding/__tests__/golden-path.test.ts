import type { ProjectReadinessSnapshot, ReadinessStep } from '@/api';
import { buildMockReadinessSnapshot } from '@/api/fixtures/readiness';
import {
  deriveGoldenPath,
  isSatisfied,
  preProjectSnapshot,
} from '@/features/onboarding/lib/golden-path';

/** Entradas do avaliador com tudo presente (equivalente ao mock "pronto"). */
function readyInputs() {
  return {
    projectId: 'p-1',
    organizationReady: true,
    accountId: 'acc-1',
    modelId: 'model-1',
    workflowBound: true,
    chiefAgentId: 'chief-1',
    chiefHealthy: true,
    chiefModelResolves: true,
  };
}

const statusOf = (snapshot: ProjectReadinessSnapshot, step: ReadinessStep) =>
  deriveGoldenPath(snapshot).steps.find((entry) => entry.id === step)?.status;

describe('isSatisfied', () => {
  it('trata Ready/Configured/Simulated como satisfeitos e o resto como não', () => {
    expect(isSatisfied('Ready')).toBe(true);
    expect(isSatisfied('Configured')).toBe(true);
    // Simulado executa (e é sinalizado como tal) — não é bloqueio.
    expect(isSatisfied('Simulated')).toBe(true);
    expect(isSatisfied('Unconfigured')).toBe(false);
    expect(isSatisfied('Degraded')).toBe(false);
    expect(isSatisfied('Unavailable')).toBe(false);
  });
});

describe('deriveGoldenPath — a partir do read model canônico', () => {
  it('apresenta as 9 etapas do contrato na ordem canônica', () => {
    const state = deriveGoldenPath(buildMockReadinessSnapshot(readyInputs()));
    expect(state.steps.map((step) => step.id)).toEqual([
      'ProfileReady',
      'OrganizationReady',
      'ProjectReady',
      'ProviderAccountReady',
      'ModelReady',
      'WorkflowReady',
      'ChiefDefinitionReady',
      'AgentPoolReady',
      'ExecutionReady',
    ]);
    expect(state.totalCount).toBe(9);
  });

  it('tudo satisfeito: caminho completo, sem etapa atual, execução liberada', () => {
    const state = deriveGoldenPath(buildMockReadinessSnapshot(readyInputs()));
    expect(state.complete).toBe(true);
    expect(state.current).toBeNull();
    expect(state.doneCount).toBe(state.totalCount);
    expect(state.canExecute).toBe(true);
  });

  it('sem workflow: workflow vira a etapa atual e a execução é bloqueada', () => {
    const snapshot = buildMockReadinessSnapshot({ ...readyInputs(), workflowBound: false });
    const state = deriveGoldenPath(snapshot);

    expect(state.current).toBe('WorkflowReady');
    expect(statusOf(snapshot, 'WorkflowReady')).toBe('current');
    expect(state.canExecute).toBe(false);
    // O bloqueio vem declarado pelo backend, não inferido aqui.
    const execution = state.steps.find((step) => step.id === 'ExecutionReady');
    expect(execution?.blockerCodes).toContain('workflow.unbound');
  });

  it('sem conta de provedor: bloqueio e ação canônica do backend', () => {
    const snapshot = buildMockReadinessSnapshot({ ...readyInputs(), accountId: null });
    const state = deriveGoldenPath(snapshot);

    expect(state.current).toBe('ProviderAccountReady');
    const step = state.steps.find((entry) => entry.id === 'ProviderAccountReady');
    expect(step?.blockerCodes).toContain('provider_account.missing');
    expect(step?.nextAction).toEqual({
      code: 'provider.connectAccount',
      route: '/providers',
      resourceId: null,
    });
    expect(state.canExecute).toBe(false);
  });

  it('chief sem saúde degrada o pool e impede execução', () => {
    const snapshot = buildMockReadinessSnapshot({ ...readyInputs(), chiefHealthy: false });
    const state = deriveGoldenPath(snapshot);

    const pool = state.steps.find((entry) => entry.id === 'AgentPoolReady');
    expect(pool?.state).toBe('Degraded');
    expect(pool?.blockerCodes).toContain('agent.degraded');
    expect(state.canExecute).toBe(false);
  });

  it('modo de execução simulado é preservado por etapa (não vira "real")', () => {
    const state = deriveGoldenPath(buildMockReadinessSnapshot(readyInputs()));
    const model = state.steps.find((entry) => entry.id === 'ModelReady');
    expect(model?.state).toBe('Simulated');
    expect(model?.executionMode).toBe('simulated');
    // Satisfeito, porém explicitamente simulado — a UI sinaliza isso.
    expect(model?.status).toBe('done');
  });

  it('a etapa atual é a primeira não satisfeita, mesmo bloqueada', () => {
    const snapshot = buildMockReadinessSnapshot({
      ...readyInputs(),
      accountId: null,
      workflowBound: false,
    });
    const state = deriveGoldenPath(snapshot);
    expect(state.current).toBe('ProviderAccountReady');
    expect(statusOf(snapshot, 'WorkflowReady')).toBe('blocked');
  });
});

describe('preProjectSnapshot — fase anterior ao projeto (endpoint não aplicável)', () => {
  it('workspace vazio: organização é a etapa atual e nada é presumido pronto', () => {
    const snapshot = preProjectSnapshot({
      hasProfile: true,
      hasOrganization: false,
      hasProject: false,
    });
    const state = deriveGoldenPath(snapshot);

    expect(state.current).toBe('OrganizationReady');
    expect(statusOf(snapshot, 'ProfileReady')).toBe('done');
    expect(state.canExecute).toBe(false);
    expect(state.doneCount).toBe(1);
  });

  it('projeto herda a pré-condição de organização declarada no contrato', () => {
    const snapshot = preProjectSnapshot({
      hasProfile: true,
      hasOrganization: false,
      hasProject: false,
    });
    const projectStep = deriveGoldenPath(snapshot).steps.find(
      (entry) => entry.id === 'ProjectReady',
    );
    expect(projectStep?.blockerCodes).toEqual(['organization.required']);
    expect(projectStep?.nextAction?.code).toBe('organization.create');
  });

  it('com organização e sem projeto, o foco vai para criar projeto', () => {
    const snapshot = preProjectSnapshot({
      hasProfile: true,
      hasOrganization: true,
      hasProject: false,
    });
    const state = deriveGoldenPath(snapshot);
    expect(state.current).toBe('ProjectReady');
    expect(
      state.steps.find((entry) => entry.id === 'ProjectReady')?.nextAction?.code,
    ).toBe('project.create');
  });

  it('sem projeto, dependências seguintes ficam bloqueadas por ausência de projeto', () => {
    const snapshot = preProjectSnapshot({
      hasProfile: true,
      hasOrganization: true,
      hasProject: false,
    });
    const workflow = deriveGoldenPath(snapshot).steps.find(
      (entry) => entry.id === 'WorkflowReady',
    );
    expect(workflow?.status).toBe('blocked');
    expect(workflow?.blockerCodes).toEqual(['project.missing']);
  });
});

describe('buildMockReadinessSnapshot — fidelidade ao avaliador canônico', () => {
  it('estado geral é o elo mais fraco das etapas', () => {
    expect(buildMockReadinessSnapshot(readyInputs()).overallState).toBe('Simulated');
    expect(
      buildMockReadinessSnapshot({ ...readyInputs(), workflowBound: false }).overallState,
    ).toBe('Unconfigured');
  });

  it('messageCode segue o padrão localizável do contrato', () => {
    const snapshot = buildMockReadinessSnapshot({ ...readyInputs(), workflowBound: false });
    const workflow = snapshot.steps.find((step) => step.step === 'WorkflowReady');
    expect(workflow?.messageCode).toBe('readiness.workflow.unconfigured');
  });

  it('nextActions agrega ações das etapas não prontas, sem repetir código', () => {
    const snapshot = buildMockReadinessSnapshot({
      ...readyInputs(),
      accountId: null,
      modelId: null,
    });
    const codes = snapshot.nextActions.map((action) => action.code);
    expect(new Set(codes).size).toBe(codes.length);
    expect(codes).toContain('provider.connectAccount');
    expect(codes).toContain('model.enable');
  });
});
