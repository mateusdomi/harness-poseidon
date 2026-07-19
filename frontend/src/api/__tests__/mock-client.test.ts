import { describe, expect, it } from 'vitest';

import { ApiError } from '../contracts';
import { MockApiClient } from '../client';
import { buildFixtures, DeterministicUlidGenerator } from '../fixtures';
import { createTestBundle } from './test-utils';

describe('MockApiClient: CRUD básico', () => {
  it('cria, lê e filtra recursos', async () => {
    const { api, fixtures } = createTestBundle();
    const project = fixtures.data.projects[0];

    const created = await api.create('solicitations', {
      projectId: project.id,
      kind: 'request',
      title: 'Nova solicitação de teste',
      body: 'Corpo da solicitação criada no teste.',
    });
    expect(created.id).toMatch(/^[0-9A-HJKMNP-TV-Z]{26}$/);
    expect(created.state).toBe('open');
    expect(created.authorProfileId).toBe(fixtures.meta.currentProfileId);

    const fetched = await api.get('solicitations', created.id);
    expect(fetched.title).toBe('Nova solicitação de teste');

    const page = await api.list('solicitations', { filter: { projectId: project.id } });
    expect(page.items.some((s) => s.id === created.id)).toBe(true);
  });

  it('atualiza recursos permitidos (settings) e rejeita get inexistente', async () => {
    const { api, fixtures } = createTestBundle();
    const settings = fixtures.data.settings[0];

    const updated = await api.update('settings', settings.id, { theme: 'light' });
    expect(updated.theme).toBe('light');
    expect(updated.language).toBe('pt-BR');

    await expect(api.get('settings', '01J9QH5Z3W8K2M4P6R8T0V2X4Y')).rejects.toMatchObject({
      problem: { status: 404 },
    });
  });

  it('remove recursos removíveis', async () => {
    const { api, fixtures } = createTestBundle();
    const prototype = fixtures.data.prototypes[0];
    await api.remove('prototypes', prototype.id);
    await expect(api.get('prototypes', prototype.id)).rejects.toBeInstanceOf(ApiError);
  });

  it('resolveApproval exige observação ao reprovar (problem+json 400)', async () => {
    const { api, fixtures } = createTestBundle();
    const pending = fixtures.data.approvals.find((a) => a.state === 'pending')!;

    await expect(api.resolveApproval(pending.id, { decision: 'rejected' })).rejects.toMatchObject({
      problem: { status: 400 },
    });

    const resolved = await api.resolveApproval(pending.id, {
      decision: 'rejected',
      note: 'Faltam evidências de teste.',
    });
    expect(resolved.state).toBe('rejected');
    expect(resolved.resolutionNote).toBe('Faltam evidências de teste.');
  });
});

describe('MockApiClient: paginação por cursor', () => {
  it('percorre todas as páginas sem duplicar itens', async () => {
    const { api, fixtures } = createTestBundle();
    const total = fixtures.data.tasks.length;
    const seen = new Set<string>();
    let cursor: string | undefined;
    let pages = 0;

    do {
      const page: Awaited<ReturnType<typeof api.list<'tasks'>>> = await api.list('tasks', {
        cursor,
        limit: 7,
      });
      pages += 1;
      for (const task of page.items) {
        expect(seen.has(task.id)).toBe(false);
        seen.add(task.id);
      }
      cursor = page.nextCursor ?? undefined;
    } while (cursor !== undefined);

    expect(seen.size).toBe(total);
    expect(pages).toBeGreaterThan(1);
  });
});

describe('MockApiClient: latência e erros simulados', () => {
  it('latência padrão não zera (100–600 ms)', async () => {
    const fixtures = buildFixtures(42);
    const ids = new DeterministicUlidGenerator(1);
    const api = new MockApiClient(fixtures, {
      nextId: () => ids.next(),
      currentProfileId: fixtures.meta.currentProfileId,
    });
    const started = performance.now();
    await api.list('projects');
    expect(performance.now() - started).toBeGreaterThanOrEqual(95);
  });

  it('queueError simula problem+json na próxima chamada', async () => {
    const { api } = createTestBundle();
    api.queueError({
      type: 'https://httpstatuses.com/503',
      title: 'Serviço indisponível',
      status: 503,
      detail: 'Cenário de indisponibilidade simulado.',
    });

    const error = await api.list('projects').catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).problem).toMatchObject({ status: 503, title: 'Serviço indisponível' });

    // Erro é consumido uma única vez; a chamada seguinte funciona.
    const page = await api.list('projects');
    expect(page.items.length).toBeGreaterThan(0);
  });

  it('failureRate simula falhas aleatórias determinísticas', async () => {
    const { api } = createTestBundle({ failureRate: 1 });
    await expect(api.list('projects')).rejects.toMatchObject({ problem: { status: 500 } });
  });
});

describe('MockApiClient: FR-4 — histórico de config de projeto', () => {
  it('alterar campo versionado incrementa configVersion e registra no histórico', async () => {
    const { api, fixtures } = createTestBundle();
    const project = fixtures.data.projects[0]; // configVersion 3, 2 entradas

    const updated = await api.update('projects', project.id, {
      technologies: ['React', 'TypeScript', 'Vite', 'Tailwind', 'Zustand'],
    });

    expect(updated.configVersion).toBe(4);
    expect(updated.configHistory).toHaveLength(3);
    const entry = updated.configHistory[2];
    expect(entry.version).toBe(4);
    expect(entry.changedFields).toEqual(['technologies']);
    expect(entry.summary).toContain('technologies');
  });

  it('editar apenas metadados NÃO gera nova versão de config', async () => {
    const { api, fixtures } = createTestBundle();
    const project = fixtures.data.projects[0];

    const updated = await api.update('projects', project.id, {
      name: 'Poseidon Frontend (renomeado)',
      description: 'Nova descrição.',
    });

    expect(updated.configVersion).toBe(3);
    expect(updated.configHistory).toHaveLength(2);
  });

  it('campo versionado enviado sem mudança NÃO gera nova versão', async () => {
    const { api, fixtures } = createTestBundle();
    const project = fixtures.data.projects[0];

    const updated = await api.update('projects', project.id, {
      defaultBranch: project.defaultBranch,
    });

    expect(updated.configVersion).toBe(3);
  });
});

describe('MockApiClient: FR-4 — ciclo de vida de templates de workflow', () => {
  it('cria template rascunho, edita rascunho, publica (congela) e bloqueia edição de publicada', async () => {
    const { api } = createTestBundle();

    const template = await api.createWorkflowTemplate({ name: 'Fluxo X' });
    expect(template.state).toBe('draft');
    expect(template.currentVersionId).toBeNull();

    const draft = await api.createWorkflowDraftVersion(template.id, {
      phases: ['Descoberta'],
    });
    expect(draft.state).toBe('draft');
    expect(draft.publishedAt).toBeNull();

    const edited = await api.updateWorkflowDraftVersion(draft.id, { phases: ['Descoberta', 'Entrega'] });
    expect(edited.phases).toEqual(['Descoberta', 'Entrega']);

    const published = await api.publishWorkflowDraft(draft.id, { changelog: 'Primeira versão.' });
    expect(published.state).toBe('published');
    expect(published.publishedAt).not.toBeNull();

    const updatedTemplate = await api.get('workflow-templates', template.id);
    expect(updatedTemplate.currentVersionId).toBe(published.id);
    expect(updatedTemplate.state).toBe('published');

    // Publicada é imutável: edição e exclusão bloqueadas (409).
    await expect(
      api.updateWorkflowDraftVersion(published.id, { phases: ['Outra'] }),
    ).rejects.toMatchObject({ problem: { status: 409 } });
    await expect(api.deleteWorkflowDraftVersion(published.id)).rejects.toMatchObject({
      problem: { status: 409 },
    });
  });

  it('publicação inválida é bloqueada pela validação do Harness (422)', async () => {
    const { api } = createTestBundle();

    const template = await api.createWorkflowTemplate({ name: 'Fluxo Inválido' });
    const draft = await api.createWorkflowDraftVersion(template.id, {
      phases: ['A', 'A'],
    });

    await expect(api.publishWorkflowDraft(draft.id)).rejects.toMatchObject({
      problem: { status: 422 },
    });

    // Rascunho permanece rascunho após a tentativa bloqueada.
    const kept = await api.get('workflow-versions', draft.id);
    expect(kept.state).toBe('draft');
  });

  it('duplicar versão cria rascunho independente; excluir rascunho em uso é bloqueado', async () => {
    const { api, fixtures } = createTestBundle();
    const published = fixtures.data['workflow-versions'].find((v) => v.state === 'published')!;

    const copy = await api.duplicateWorkflowVersion(published.id);
    expect(copy.state).toBe('draft');
    expect(copy.version).toBe(published.version + 1);
    expect(copy.id).not.toBe(published.id);

    // A versão publicada está em uso (workflow ativo): nunca excluível.
    await expect(api.deleteWorkflowDraftVersion(published.id)).rejects.toMatchObject({
      problem: { status: 409 },
    });

    // Template utilizado não pode ser excluído, mas pode ser arquivado.
    await expect(api.deleteWorkflowTemplate(published.templateId)).rejects.toMatchObject({
      problem: { status: 409 },
    });
    const archived = await api.archiveWorkflowTemplate(published.templateId);
    expect(archived.state).toBe('archived');
    expect(archived.archivedAt).not.toBeNull();
  });

  it('vincula template a projeto sem workflow; 409 quando já vinculado', async () => {
    const { api, fixtures } = createTestBundle();
    const template = fixtures.data['workflow-templates'].find((tpl) => tpl.state === 'published')!;
    const projectWithWorkflow = fixtures.data.projects[0];

    await expect(
      api.linkWorkflowTemplate({ projectId: projectWithWorkflow.id, templateId: template.id }),
    ).rejects.toMatchObject({ problem: { status: 409 } });

    const newProject = await api.create('projects', {
      organizationId: fixtures.data.organizations[0].id,
      name: 'Projeto Novo',
      key: 'NOVO',
      description: 'Sem workflow ainda.',
    });
    const workflow = await api.linkWorkflowTemplate({
      projectId: newProject.id,
      templateId: template.id,
    });
    expect(workflow.projectId).toBe(newProject.id);
    expect(workflow.activeVersionId).toBe(template.currentVersionId);
  });
});
