import type { Account, Model, Organization, Project, Provider, Workflow } from '@/api';
import {
  deriveGoldenPath,
  isModelReady,
  isProviderReady,
  type GoldenPathInput,
} from '@/features/onboarding/lib/golden-path';

const org = (id: string) => ({ id }) as Organization;
const project = (id: string, chiefAgentId = 'chief-1') =>
  ({ id, organizationId: 'org-1', chiefAgentId }) as Project;
const provider = (id: string, enabled: boolean) => ({ id, enabled }) as Provider;
const account = (providerId: string, state: Account['state']) =>
  ({ id: `acc-${providerId}`, providerId, state }) as Account;
const model = (providerId: string, enabled: boolean) =>
  ({ id: `m-${providerId}`, providerId, enabled }) as Model;
const workflow = () => ({ id: 'wf-1' }) as Workflow;

/** Entrada base: workspace completamente vazio (só perfil ativo). */
function emptyInput(): GoldenPathInput {
  return {
    hasProfile: true,
    organizations: [],
    projects: [],
    activeProject: null,
    providers: [],
    accounts: [],
    models: [],
    activeWorkflow: null,
    hasChiefActivity: false,
  };
}

/** Entrada com todas as pré-condições reais satisfeitas. */
function readyInput(): GoldenPathInput {
  return {
    hasProfile: true,
    organizations: [org('org-1')],
    projects: [project('p-1')],
    activeProject: project('p-1'),
    providers: [provider('prov-1', true)],
    accounts: [account('prov-1', 'active')],
    models: [model('prov-1', true)],
    activeWorkflow: workflow(),
    hasChiefActivity: true,
  };
}

describe('isProviderReady', () => {
  it('exige provedor habilitado E conta ativa do mesmo provedor', () => {
    expect(isProviderReady([provider('a', true)], [account('a', 'active')])).toBe(true);
    expect(isProviderReady([provider('a', false)], [account('a', 'active')])).toBe(false);
    expect(isProviderReady([provider('a', true)], [account('a', 'disabled')])).toBe(false);
    // conta ativa mas de outro provedor não conta
    expect(isProviderReady([provider('a', true)], [account('b', 'active')])).toBe(false);
    expect(isProviderReady([], [])).toBe(false);
  });
});

describe('isModelReady', () => {
  it('exige modelo habilitado de um provedor habilitado', () => {
    expect(isModelReady([model('a', true)], [provider('a', true)])).toBe(true);
    expect(isModelReady([model('a', false)], [provider('a', true)])).toBe(false);
    expect(isModelReady([model('a', true)], [provider('a', false)])).toBe(false);
  });
});

describe('deriveGoldenPath', () => {
  it('workspace vazio: perfil pronto, organização é a etapa atual, resto pendente/bloqueado', () => {
    const state = deriveGoldenPath(emptyInput());
    expect(state.doneCount).toBe(1); // só o perfil
    expect(state.complete).toBe(false);
    expect(state.current).toBe('organization');
    const byId = Object.fromEntries(state.steps.map((s) => [s.id, s.status]));
    expect(byId.profile).toBe('done');
    expect(byId.organization).toBe('current');
    // projeto depende de organização → bloqueado
    expect(byId.project).toBe('blocked');
    // provedor não depende de nada → pendente (acionável, mas não é o foco)
    expect(byId.provider).toBe('pending');
    // modelo depende de provedor → bloqueado
    expect(byId.model).toBe('blocked');
  });

  it('project blocked lista organization como pré-requisito', () => {
    const projectStep = deriveGoldenPath(emptyInput()).steps.find((s) => s.id === 'project');
    expect(projectStep?.blockedBy).toEqual(['organization']);
  });

  it('tudo pronto: caminho completo e sem etapa atual', () => {
    const state = deriveGoldenPath(readyInput());
    expect(state.complete).toBe(true);
    expect(state.doneCount).toBe(state.totalCount);
    expect(state.current).toBeNull();
  });

  it('chief é fail-closed: sem workflow, chief e primeira execução não ficam prontos', () => {
    const input = { ...readyInput(), activeWorkflow: null };
    const byId = Object.fromEntries(
      deriveGoldenPath(input).steps.map((s) => [s.id, s.status]),
    );
    expect(byId.workflow).toBe('current');
    expect(byId.chief).toBe('blocked');
    expect(byId.firstRun).toBe('blocked');
  });

  it('primeira execução só conclui com sinal real de atividade do chief', () => {
    const input = { ...readyInput(), hasChiefActivity: false };
    const state = deriveGoldenPath(input);
    const firstRun = state.steps.find((s) => s.id === 'firstRun');
    expect(firstRun?.status).toBe('current');
    expect(state.complete).toBe(false);
  });

  it('provedor sem conta ativa fica como etapa atual e o chief não pode ficar pronto', () => {
    const input: GoldenPathInput = {
      ...readyInput(),
      accounts: [account('prov-1', 'disabled')],
    };
    const byId = Object.fromEntries(deriveGoldenPath(input).steps.map((s) => [s.id, s.status]));
    expect(byId.provider).toBe('current');
    // modelo é fato independente (catálogo do provedor), pode estar pronto…
    expect(byId.model).toBe('done');
    // …mas o chief exige provedor + modelo + workflow → bloqueado sem conta ativa
    expect(byId.chief).toBe('blocked');
    expect(byId.firstRun).toBe('blocked');
  });
});
