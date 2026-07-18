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
