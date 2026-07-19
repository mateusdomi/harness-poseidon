import { validateWorkflowVersionContent, type WorkflowVersion } from '@/api';
import { diffWorkflowVersions } from '@/features/workflows/lib/version-diff';

function version(overrides: Partial<WorkflowVersion>): WorkflowVersion {
  return {
    id: '01J00000000000000000000000',
    templateId: '01J00000000000000000000001',
    version: 1,
    phases: ['A', 'B'],
    gatesByPhase: { B: ['Gate 1'] },
    changelog: null,
    state: 'published',
    publishedAt: '2026-07-17T12:00:00Z',
    archivedAt: null,
    ...overrides,
  };
}

describe('validateWorkflowVersionContent', () => {
  it('aprova versão válida', () => {
    const issues = validateWorkflowVersionContent(version({}));
    expect(issues).toEqual([]);
  });

  it('exige ao menos uma fase', () => {
    const issues = validateWorkflowVersionContent({ phases: [] });
    expect(issues.map((issue) => issue.key)).toContain(
      'workflows.templates.validation.phasesRequired',
    );
  });

  it('detecta fase duplicada e nome vazio', () => {
    const issues = validateWorkflowVersionContent({ phases: ['A', 'A', '  '] });
    const keys = issues.map((issue) => issue.key);
    expect(keys).toContain('workflows.templates.validation.duplicatePhase');
    expect(keys).toContain('workflows.templates.validation.emptyPhaseName');
  });

  it('detecta gates e transições referenciando fases inexistentes', () => {
    const issues = validateWorkflowVersionContent({
      phases: ['A'],
      gatesByPhase: { Fantasma: ['G'] },
      transitions: { A: ['Fantasma'] },
    });
    const keys = issues.map((issue) => issue.key);
    expect(keys).toContain('workflows.templates.validation.unknownGatePhase');
    expect(keys).toContain('workflows.templates.validation.unknownTransitionTarget');
  });

  it('detecta dependência inexistente e ciclo simples', () => {
    const missing = validateWorkflowVersionContent({
      phases: ['A', 'B'],
      phaseConfigs: {
        A: { documentKinds: [], progressWeight: 50, allowedAgentDefinitionIds: [], dependsOn: ['Fantasma'] },
      },
    });
    expect(missing.map((issue) => issue.key)).toContain(
      'workflows.templates.validation.unknownDependency',
    );

    const cycle = validateWorkflowVersionContent({
      phases: ['A', 'B'],
      phaseConfigs: {
        A: { documentKinds: [], progressWeight: 50, allowedAgentDefinitionIds: [], dependsOn: ['B'] },
        B: { documentKinds: [], progressWeight: 50, allowedAgentDefinitionIds: [], dependsOn: ['A'] },
      },
    });
    expect(cycle.map((issue) => issue.key)).toContain(
      'workflows.templates.validation.dependencyCycle',
    );
  });

  it('detecta peso fora da faixa 0–100', () => {
    const issues = validateWorkflowVersionContent({
      phases: ['A'],
      phaseConfigs: {
        A: { documentKinds: [], progressWeight: 120, allowedAgentDefinitionIds: [] },
      },
    });
    expect(issues.map((issue) => issue.key)).toContain(
      'workflows.templates.validation.invalidWeight',
    );
  });
});

describe('diffWorkflowVersions', () => {
  it('versões idênticas não têm diff', () => {
    const diff = diffWorkflowVersions(version({}), version({ version: 2 }));
    expect(diff.identical).toBe(true);
  });

  it('detecta fases adicionadas, removidas e reordenadas', () => {
    const before = version({ phases: ['A', 'B', 'C'] });
    const after = version({ version: 2, phases: ['C', 'A', 'D'] });
    const diff = diffWorkflowVersions(before, after);
    expect(diff.phasesAdded).toEqual(['D']);
    expect(diff.phasesRemoved).toEqual(['B']);
    expect(diff.phasesReordered).toEqual(['C', 'A']);
    expect(diff.identical).toBe(false);
  });

  it('detecta mudanças de gates, peso e campos estendidos da fase', () => {
    const before = version({
      phaseConfigs: {
        A: {
          documentKinds: ['prd'],
          progressWeight: 25,
          allowedAgentDefinitionIds: [],
          objective: 'Antes',
          allowedSkillIds: ['s1'],
        },
      },
    });
    const after = version({
      version: 2,
      gatesByPhase: { B: ['Gate 1', 'Gate 2'] },
      phaseConfigs: {
        A: {
          documentKinds: ['prd', 'spec'],
          progressWeight: 40,
          allowedAgentDefinitionIds: [],
          objective: 'Depois',
          allowedSkillIds: [],
          acceptanceCriteria: ['Critério novo'],
        },
      },
    });
    const diff = diffWorkflowVersions(before, after);
    const phaseA = diff.phasesChanged.find((change) => change.phase === 'A')!;
    const phaseB = diff.phasesChanged.find((change) => change.phase === 'B')!;

    expect(phaseB.lists.find((change) => change.field === 'gates')?.added).toEqual(['Gate 2']);
    expect(phaseA.values.find((change) => change.field === 'weight')).toEqual({
      field: 'weight',
      from: '25',
      to: '40',
    });
    expect(phaseA.values.find((change) => change.field === 'objective')).toEqual({
      field: 'objective',
      from: 'Antes',
      to: 'Depois',
    });
    expect(phaseA.lists.find((change) => change.field === 'documentKinds')?.added).toEqual(['spec']);
    expect(phaseA.lists.find((change) => change.field === 'skills')?.removed).toEqual(['s1']);
    expect(phaseA.lists.find((change) => change.field === 'acceptanceCriteria')?.added).toEqual([
      'Critério novo',
    ]);
  });

  it('detecta mudança do modo de operação padrão', () => {
    const before = version({ defaultOperationMode: 'manual' });
    const after = version({ version: 2, defaultOperationMode: 'autonomous' });
    const diff = diffWorkflowVersions(before, after);
    expect(diff.modeChange).toEqual({ from: 'manual', to: 'autonomous' });
  });
});
