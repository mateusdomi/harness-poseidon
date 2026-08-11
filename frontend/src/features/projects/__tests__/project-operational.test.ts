import { buildFixtures } from '@/api';
import { deriveProjectOperationalSummary } from '@/features/projects/lib/project-operational';

describe('projeção operacional dos projetos', () => {
  it('deriva lifecycle V3, responsável, saúde e execução ativa apenas das fontes reais', () => {
    const fixtures = buildFixtures(42).data;
    const project = fixtures.projects[0];
    const summary = deriveProjectOperationalSummary(project, {
      tasks: fixtures.tasks,
      agents: fixtures.agents,
      workflows: fixtures.workflows,
      runs: fixtures['workflow-runs'],
      phases: fixtures.phases,
    });

    expect(summary.lifecycleState).toBe('build');
    expect(summary.responsibleName).toBe('Bruna Magalhães');
    expect(summary.runningExecutions).toBe(1);
    expect(['healthy', 'attention', 'unavailable']).toContain(summary.health);
  });
});
