import { describe, expect, it } from 'vitest';

import { ApiError } from '@/api';
import { MockArchitectureApi } from '../api/architecture-api';

const PROJECT = 'PROJ1';

describe('MockArchitectureApi', () => {
  it('semeia um modelo determinístico cobrindo C4 e ArchiMate', async () => {
    const api = new MockArchitectureApi();
    const elements = await api.listElements(PROJECT);
    const relationships = await api.listRelationships(PROJECT);

    expect(elements.length).toBeGreaterThan(10);
    expect(relationships.length).toBeGreaterThan(10);
    // Kinds representativos das duas notações presentes.
    const kinds = new Set(elements.map((e) => e.kind));
    expect(kinds.has('person')).toBe(true);
    expect(kinds.has('softwareSystem')).toBe(true);
    expect(kinds.has('dataObject')).toBe(true);
    expect(kinds.has('goal')).toBe(true);
  });

  it('lista views salvas com contagem de elementos', async () => {
    const api = new MockArchitectureApi();
    const views = await api.listViews(PROJECT);
    expect(views.length).toBeGreaterThanOrEqual(2);
    expect(views.every((v) => v.elementCount > 0)).toBe(true);
    const notations = new Set(views.map((v) => v.notation));
    expect(notations.has('c4')).toBe(true);
    expect(notations.has('archimate')).toBe(true);
  });

  it('resolve uma view completa por id', async () => {
    const api = new MockArchitectureApi();
    const [summary] = await api.listViews(PROJECT);
    const view = await api.getView(summary.id);
    expect(view.id).toBe(summary.id);
    expect(view.elements.length).toBe(summary.elementCount);
  });

  it('cria elemento, alterna lock e persiste versão', async () => {
    const api = new MockArchitectureApi();
    const created = await api.createElement({
      projectId: PROJECT,
      kind: 'container',
      name: 'Novo Serviço',
      description: 'teste',
      properties: {},
    });
    expect(created.locked).toBe(false);
    expect(created.version).toBe(1);

    const locked = await api.setElementLock(created.id, { locked: true });
    expect(locked.locked).toBe(true);
    expect(locked.version).toBe(2);

    const elements = await api.listElements(PROJECT);
    expect(elements.find((e) => e.id === created.id)?.locked).toBe(true);
  });

  it('cria relacionamento e view a partir dos elementos', async () => {
    const api = new MockArchitectureApi();
    const a = await api.createElement({
      projectId: PROJECT,
      kind: 'container',
      name: 'A',
      description: '',
      properties: {},
    });
    const b = await api.createElement({
      projectId: PROJECT,
      kind: 'dataObject',
      name: 'B',
      description: '',
      properties: {},
    });
    const rel = await api.createRelationship({
      projectId: PROJECT,
      sourceId: a.id,
      targetId: b.id,
      kind: 'uses',
      properties: {},
    });
    expect(rel.sourceId).toBe(a.id);

    const view = await api.createView({
      projectId: PROJECT,
      name: 'Minha view',
      description: '',
      notation: 'c4',
      elementIds: [a.id, b.id],
      relationshipIds: [rel.id],
      filterKinds: [],
      filterTags: [],
    });
    expect(view.elements).toHaveLength(2);
    expect(view.relationships).toHaveLength(1);
  });

  it('lança 404 ao buscar view inexistente', async () => {
    const api = new MockArchitectureApi();
    await expect(api.getView('missing')).rejects.toBeInstanceOf(ApiError);
  });
});
