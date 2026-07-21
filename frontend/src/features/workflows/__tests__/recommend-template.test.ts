import type { WorkflowTemplate, WorkflowVersion } from '@/api';
import { recommendWorkflowTemplate } from '@/features/workflows/lib/recommend-template';

function template(
  id: string,
  overrides: Partial<WorkflowTemplate> = {},
): WorkflowTemplate {
  return {
    id,
    name: `Template ${id}`,
    description: '',
    currentVersionId: `v-${id}`,
    state: 'published',
    archivedAt: null,
    createdAt: '2026-07-01T00:00:00Z',
    ...overrides,
  } as WorkflowTemplate;
}

function version(
  id: string,
  templateId: string,
  phases: string[],
  overrides: Partial<WorkflowVersion> = {},
): WorkflowVersion {
  return {
    id,
    templateId,
    version: 1,
    phases,
    gatesByPhase: {},
    changelog: null,
    state: 'published',
    publishedAt: '2026-07-01T00:00:00Z',
    archivedAt: null,
    ...overrides,
  } as WorkflowVersion;
}

describe('recommendWorkflowTemplate', () => {
  it('sem templates publicados não recomenda nada (fail-closed)', () => {
    expect(recommendWorkflowTemplate([], [])).toBeNull();
  });

  it('ignora template sem versão publicada vigente', () => {
    const t1 = template('a', { currentVersionId: null });
    expect(recommendWorkflowTemplate([t1], [])).toBeNull();

    // currentVersionId aponta para uma versão ainda em rascunho
    const t2 = template('b');
    const draft = version('v-b', 'b', ['Fase'], { state: 'draft' });
    expect(recommendWorkflowTemplate([t2], [draft])).toBeNull();
  });

  it('ignora templates arquivados', () => {
    const archived = template('a', { state: 'archived' });
    const v = version('v-a', 'a', ['Uma', 'Duas']);
    expect(recommendWorkflowTemplate([archived], [v])).toBeNull();
  });

  it('prefere o template com mais fases', () => {
    const small = template('small');
    const big = template('big');
    const versions = [
      version('v-small', 'small', ['Uma']),
      version('v-big', 'big', ['Uma', 'Duas', 'Três']),
    ];
    const result = recommendWorkflowTemplate([small, big], versions);
    expect(result?.template.id).toBe('big');
    expect(result?.version.phases).toHaveLength(3);
  });

  it('empate em fases resolve pelo template mais antigo (estável)', () => {
    const older = template('older', { createdAt: '2026-06-01T00:00:00Z' });
    const newer = template('newer', { createdAt: '2026-07-10T00:00:00Z' });
    const versions = [
      version('v-older', 'older', ['Uma', 'Duas']),
      version('v-newer', 'newer', ['Uma', 'Duas']),
    ];
    expect(recommendWorkflowTemplate([newer, older], versions)?.template.id).toBe('older');
  });
});
