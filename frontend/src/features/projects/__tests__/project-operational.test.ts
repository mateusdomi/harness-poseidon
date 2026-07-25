import { buildFixtures } from '@/api';
import { deriveProjectOperationalSummary } from '@/features/projects/lib/project-operational';

describe('projeção operacional dos projetos', () => {
  it('deriva fase, progresso, responsável, saúde e workflow apenas das fontes reais', () => {
    const fixtures = buildFixtures(42).data;
    const project = fixtures.projects[0];
    const summary = deriveProjectOperationalSummary(project, {
      tasks: fixtures.tasks,
      agents: fixtures.agents,
      workflows: fixtures.workflows,
      runs: fixtures['workflow-runs'],
      phases: fixtures.phases,
      templates: fixtures['workflow-templates'],
    });

    expect(summary.totalTasks).toBe(
      fixtures.tasks.filter((task) => task.projectId === project.id).length,
    );
    expect(summary.responsibleName).toBe('Bruna Magalhães');
    expect(summary.workflowName).toBeTruthy();
    expect(summary.progressPercent === null || summary.progressPercent >= 0).toBe(true);
  });
});
