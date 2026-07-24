import { describe, expect, it } from 'vitest';

import type { ArchitectureElement, ArchitectureRelationship } from '../api/types';
import { layoutModel } from '../model/layout';

function element(id: string, kind: string, name = id): ArchitectureElement {
  return {
    id,
    projectId: 'p1',
    kind,
    name,
    description: '',
    properties: {},
    state: 'baseline',
    locked: false,
    version: 1,
  };
}

describe('layoutModel', () => {
  it('posiciona elementos em colunas por camada', () => {
    const elements = [
      element('person', 'person'),
      element('api', 'container'),
      element('db', 'dataObject'),
    ];
    const layout = layoutModel(elements, []);
    expect(layout.nodes).toHaveLength(3);
    const person = layout.nodes.find((n) => n.element.id === 'person')!;
    const api = layout.nodes.find((n) => n.element.id === 'api')!;
    const db = layout.nodes.find((n) => n.element.id === 'db')!;
    // Contexto antes de aplicação antes de dados (colunas crescentes).
    expect(person.column).toBeLessThan(api.column);
    expect(api.column).toBeLessThan(db.column);
    expect(person.x).toBeLessThan(api.x);
  });

  it('empilha elementos da mesma coluna em linhas distintas, ordenados por nome', () => {
    const elements = [
      element('b', 'container', 'Bravo'),
      element('a', 'container', 'Alpha'),
    ];
    const layout = layoutModel(elements, []);
    const alpha = layout.nodes.find((n) => n.element.id === 'a')!;
    const bravo = layout.nodes.find((n) => n.element.id === 'b')!;
    expect(alpha.column).toBe(bravo.column);
    expect(alpha.y).toBeLessThan(bravo.y);
  });

  it('cria arestas só quando ambos os extremos existem', () => {
    const elements = [element('a', 'container'), element('b', 'dataObject')];
    const relationships: ArchitectureRelationship[] = [
      {
        id: 'r1',
        projectId: 'p1',
        sourceId: 'a',
        targetId: 'b',
        kind: 'uses',
        properties: {},
        state: 'baseline',
        version: 1,
      },
      {
        id: 'r2',
        projectId: 'p1',
        sourceId: 'a',
        targetId: 'missing',
        kind: 'uses',
        properties: {},
        state: 'baseline',
        version: 1,
      },
    ];
    const layout = layoutModel(elements, relationships);
    expect(layout.edges).toHaveLength(1);
    expect(layout.edges[0].relationship.id).toBe('r1');
  });

  it('dimensiona o canvas conforme colunas e linhas', () => {
    const layout = layoutModel([element('a', 'container')], []);
    expect(layout.width).toBeGreaterThan(0);
    expect(layout.height).toBeGreaterThan(0);
  });
});
